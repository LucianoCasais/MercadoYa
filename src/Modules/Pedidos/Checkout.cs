using MercadoYa.Shared;
using Microsoft.Extensions.Logging;

namespace MercadoYa.Modules.Pedidos;

// ---- DTOs de la API (POST /api/v1/orders) ----
public sealed record OrderLineRequest(int ProductId, int Quantity);
public sealed record CreateOrderRequest(IReadOnlyList<OrderLineRequest>? Items);
public sealed record OrderLineResponse(int ProductId, string ProductName, long UnitPriceCents, int Quantity);
public sealed record OrderResponse(Guid Id, string Status, string? Reason, long TotalCents, DateTimeOffset CreatedAt, IReadOnlyList<OrderLineResponse> Items, string BuyerName = "");

// ---- Puertos: el modulo Pedidos no conoce a Catalogo, Inventario ni Payments, solo estas interfaces. ----
public sealed record ProductInfo(int Id, string Name, long PriceCents, bool Active);
public enum StockReserveOutcome { Reserved, InsufficientStock, Unavailable }
public enum PaymentOutcome { Approved, Rejected, Unavailable }
public sealed record PaymentResult(PaymentOutcome Outcome, string? Reason = null);

public interface IProductLookup
{
    /// Devuelve los productos que existen (activos o no) entre los ids pedidos.
    Task<IReadOnlyDictionary<int, ProductInfo>> GetAsync(IReadOnlyCollection<int> ids, CancellationToken ct);
}

public interface IStockClient
{
    Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct);
    /// Idempotente: liberar un pedido sin reserva (o ya liberado) no hace nada.
    Task ReleaseAsync(Guid orderId, CancellationToken ct);
}

public interface IPaymentGateway
{
    Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct);
}

public interface IOrderRepository
{
    Task<Order?> FindByKeyAsync(string userId, string idempotencyKey, CancellationToken ct);
    /// Pedido del usuario indicado; null si no existe o es de otro usuario.
    Task<Order?> FindByIdAsync(string userId, Guid orderId, CancellationToken ct);
    /// false si ya existe un pedido con el mismo (UserId, IdempotencyKey).
    Task<bool> TryAddAsync(Order order, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

/// Accepted = el pedido quedo Pendiente y se publico a RabbitMQ (modo Async, responde 202). BrokerDown = no se pudo publicar (503).
public sealed record PlaceOrderResult(OrderResponse? Order, string? Error, bool Replayed, bool Accepted = false, bool BrokerDown = false)
{
    public bool IsValid => Error is null;
    public static PlaceOrderResult Fail(string error) => new(null, error, false);
}

/// Checkout. Dos modos (Checkout:Mode, ver CheckoutOptions):
///  - Sync (por defecto, dias 3-7): crea el pedido, reserva stock, cobra y lo deja en su estado final dentro de la request.
///  - Async (dia 8+): crea el pedido Pendiente, publica OrderRequested en RabbitMQ (con confirms) y devuelve 202; la SAGA
///    (reservar, cobrar, confirmar o compensar) la ejecuta el worker. Si no se puede publicar, el pedido queda Rechazado
///    (BrokerNoDisponible) y la API responde 503: no quedan pedidos huerfanos.
/// Las compensaciones (liberar stock) siguen la logica de la SAGA documentada.
public sealed class CheckoutService(IProductLookup products, IStockClient stock, IPaymentGateway payments,
    IOrderRepository orders, ILogger<CheckoutService> logger, IOrderPublisher? publisher = null, CheckoutOptions? options = null)
{
    public const int MaxLines = 50;
    public const int MaxQuantityPerLine = 99;
    public const int MaxIdempotencyKeyLength = 100;

    /// buyerName sale del token (nunca del cuerpo de la request) y se guarda en el pedido.
    public async Task<PlaceOrderResult> PlaceAsync(string userId, string? idempotencyKey, CreateOrderRequest? request, CancellationToken ct,
        string? buyerName = null, string? correlationId = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) return PlaceOrderResult.Fail("Usuario no identificado.");
        var key = idempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > MaxIdempotencyKeyLength)
            return PlaceOrderResult.Fail("Falta el header Idempotency-Key (hasta 100 caracteres).");

        var lines = Normalize(request?.Items, out var error);
        if (lines is null) return PlaceOrderResult.Fail(error!);

        // Reintento de una request ya procesada (doble clic, reintento de red): se devuelve el mismo pedido.
        var existing = await orders.FindByKeyAsync(userId, key, ct);
        if (existing is not null) return new PlaceOrderResult(ToResponse(existing), null, true);

        var found = await products.GetAsync(lines.Select(l => l.ProductId).ToArray(), ct);
        foreach (var l in lines)
            if (!found.TryGetValue(l.ProductId, out var p) || !p.Active)
                return PlaceOrderResult.Fail($"El producto {l.ProductId} no existe o no esta disponible.");

        var order = new Order { UserId = userId, IdempotencyKey = key, BuyerName = BuyerInfo.Clean(buyerName) };
        foreach (var l in lines)
        {
            var p = found[l.ProductId];
            order.Items.Add(new OrderItem { OrderId = order.Id, ProductId = p.Id, ProductName = p.Name, UnitPriceCents = p.PriceCents, Quantity = l.Quantity });
        }
        order.TotalCents = order.Items.Sum(i => i.UnitPriceCents * i.Quantity);   // precios congelados al comprar

        if (!await orders.TryAddAsync(order, ct))
        {
            // Dos requests simultaneas con la misma clave: gano la otra.
            existing = await orders.FindByKeyAsync(userId, key, ct);
            return existing is null
                ? PlaceOrderResult.Fail("No se pudo registrar el pedido.")
                : new PlaceOrderResult(ToResponse(existing), null, true);
        }

        if (options?.Mode == CheckoutMode.Async) return await PublishAsync(order, correlationId, ct);

        // Paso 2 de la SAGA: reservar stock.
        var reserve = await ReserveAsync(order, lines, ct);
        if (reserve != StockReserveOutcome.Reserved)
        {
            if (reserve == StockReserveOutcome.Unavailable) await ReleaseSafelyAsync(order.Id, ct);   // por si la reserva se aplico
            order.TryTransition(OrderStatus.Rechazado,
                reserve == StockReserveOutcome.InsufficientStock ? RejectionReason.SinStock : RejectionReason.StockNoDisponible);
            await orders.SaveAsync(ct);
            return new PlaceOrderResult(ToResponse(order), null, false);
        }

        // Paso 3 de la SAGA: cobrar. Si falla, compensar liberando el stock.
        var payment = await ChargeAsync(order, ct);
        switch (payment.Outcome)
        {
            case PaymentOutcome.Approved:
                order.TryTransition(OrderStatus.Confirmado);
                break;
            case PaymentOutcome.Rejected:
                await ReleaseSafelyAsync(order.Id, ct);
                order.TryTransition(OrderStatus.Rechazado, RejectionReason.PagoFallido);
                break;
            default:
                await ReleaseSafelyAsync(order.Id, ct);
                order.TryTransition(OrderStatus.Rechazado, RejectionReason.PagoNoDisponible);
                break;
        }
        await orders.SaveAsync(ct);
        return new PlaceOrderResult(ToResponse(order), null, false);
    }

    /// Modo Async: el pedido ya esta guardado como Pendiente; se publica OrderRequested y se responde sin esperar a la SAGA.
    private async Task<PlaceOrderResult> PublishAsync(Order order, string? correlationId, CancellationToken ct)
    {
        if (publisher is null) throw new InvalidOperationException("Checkout:Mode=Async requiere un IOrderPublisher registrado (AddOrderMessaging).");
        try
        {
            await publisher.PublishAsync(order.ToRequestedMessage(), correlationId ?? Guid.NewGuid().ToString("N"), ct);
            return new PlaceOrderResult(ToResponse(order), null, false, Accepted: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Sin mensaje no hay quien procese el pedido: se rechaza ya (nada que compensar, todavia no se reservo stock).
            logger.LogError(ex, "No se pudo publicar el pedido {OrderId}; se rechaza por BrokerNoDisponible", order.Id);
            order.TryTransition(OrderStatus.Rechazado, RejectionReason.BrokerNoDisponible);
            await orders.SaveAsync(ct);
            return new PlaceOrderResult(ToResponse(order), "No pudimos enviar el pedido a procesar.", false, BrokerDown: true);
        }
    }

    /// Consulta de un pedido propio (seguimiento). Devuelve null si no existe o pertenece a otro usuario.
    public async Task<OrderResponse?> GetAsync(string userId, Guid orderId, CancellationToken ct)
    {
        var order = await orders.FindByIdAsync(userId, orderId, ct);
        return order is null ? null : ToResponse(order);
    }

    /// Valida y unifica las lineas: al menos una, cantidades de 1 a 99, ids positivos, sin ids repetidos (se suman).
    public static IReadOnlyList<OrderLineRequest>? Normalize(IReadOnlyList<OrderLineRequest>? items, out string? error)
    {
        error = null;
        if (items is null || items.Count == 0) { error = "El pedido debe tener al menos un item."; return null; }
        if (items.Any(i => i.ProductId <= 0)) { error = "Hay un producto con id invalido."; return null; }
        if (items.Any(i => i.Quantity <= 0)) { error = "Las cantidades deben ser mayores a 0."; return null; }

        var merged = items.GroupBy(i => i.ProductId)
            .Select(g => new OrderLineRequest(g.Key, g.Sum(x => x.Quantity)))
            .OrderBy(l => l.ProductId)
            .ToList();
        if (merged.Count > MaxLines) { error = $"El pedido no puede tener mas de {MaxLines} productos distintos."; return null; }
        if (merged.Any(l => l.Quantity > MaxQuantityPerLine)) { error = $"La cantidad maxima por producto es {MaxQuantityPerLine}."; return null; }
        return merged;
    }

    public static OrderResponse ToResponse(Order o) => new(
        o.Id, o.Status.ToString(), o.Reason?.ToString(), o.TotalCents, o.CreatedAt,
        o.Items.OrderBy(i => i.ProductId).Select(i => new OrderLineResponse(i.ProductId, i.ProductName, i.UnitPriceCents, i.Quantity)).ToList(),
        o.BuyerName);

    private async Task<StockReserveOutcome> ReserveAsync(Order order, IReadOnlyList<OrderLineRequest> lines, CancellationToken ct)
    {
        try { return await stock.ReserveAsync(order.Id, lines, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Fallo la reserva de stock del pedido {OrderId}", order.Id);
            return StockReserveOutcome.Unavailable;
        }
    }

    private async Task<PaymentResult> ChargeAsync(Order order, CancellationToken ct)
    {
        try { return await payments.ChargeAsync(order.Id, order.TotalCents, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Fallo el cobro del pedido {OrderId}", order.Id);
            return new PaymentResult(PaymentOutcome.Unavailable, "Servicio de pagos no disponible");
        }
    }

    private async Task ReleaseSafelyAsync(Guid orderId, CancellationToken ct)
    {
        try { await stock.ReleaseAsync(orderId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Compensacion fallida: en la SAGA asincrona (dia 8+) esto deja el mensaje sin ack para reintentarlo.
            logger.LogError(ex, "No se pudo liberar el stock del pedido {OrderId}", orderId);
        }
    }
}
