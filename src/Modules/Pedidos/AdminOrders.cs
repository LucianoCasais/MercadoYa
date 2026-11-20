using MercadoYa.Shared;
using Microsoft.Extensions.Logging;

namespace MercadoYa.Modules.Pedidos;

// ---- DTOs de la API de admin de pedidos (/api/v1/admin/orders) ----
public sealed record AdminOrderSummary(Guid Id, string BuyerName, string Status, string? Reason, long TotalCents,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int ItemCount);
public sealed record AdminOrderPage(IReadOnlyList<AdminOrderSummary> Items, int Page, int PageSize, int Total);
/// PATCH /admin/orders/{id}/status. Solo se aceptan "Entregado" y "Cancelado".
public sealed record ChangeOrderStatusRequest(string? Status);

/// Puerto de acceso de admin a los pedidos (de cualquier usuario). Lo implementa EfOrderRepository.
public interface IAdminOrderRepository
{
    /// Pedidos mas nuevos primero, con filtro opcional por estado.
    Task<(IReadOnlyList<Order> Items, int Total)> ListAsync(OrderStatus? status, int page, int pageSize, CancellationToken ct);
    /// Pedido con sus items, rastreado para poder guardarlo despues de cambiarle el estado.
    Task<Order?> FindAsync(Guid orderId, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public enum ChangeStatusOutcome { Changed, NotFound, InvalidStatus, InvalidTransition, StockReleaseFailed }
public sealed record ChangeStatusResult(ChangeStatusOutcome Outcome, OrderResponse? Order = null, string? Detail = null);

/// Gestion de pedidos por el administrador: listar y pasar un pedido Confirmado a Entregado o Cancelado.
/// Cancelar libera el stock (operacion idempotente) ANTES de cambiar el estado: si la liberacion falla el pedido sigue
/// Confirmado y el admin puede reintentar sin riesgo de liberar dos veces.
public sealed class AdminOrderService(IAdminOrderRepository orders, IStockClient stock, ILogger<AdminOrderService> logger)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// Acepta el nombre del estado sin distinguir mayusculas; rechaza numeros y valores desconocidos.
    public static bool TryParseStatus(string? text, out OrderStatus status)
    {
        status = default;
        var t = text?.Trim();
        return !string.IsNullOrEmpty(t) && !int.TryParse(t, out _) && Enum.TryParse(t, ignoreCase: true, out status) && Enum.IsDefined(status);
    }

    public async Task<(AdminOrderPage? Page, string? Error)> ListAsync(string? status, int page, int pageSize, CancellationToken ct)
    {
        OrderStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!TryParseStatus(status, out var s)) return (null, "El estado no es valido (Pendiente, Confirmado, Rechazado, Entregado o Cancelado).");
            filter = s;
        }
        page = Math.Max(page, 1);
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        var (items, total) = await orders.ListAsync(filter, page, pageSize, ct);
        var summaries = items.Select(o => new AdminOrderSummary(o.Id, o.BuyerName, o.Status.ToString(), o.Reason?.ToString(),
            o.TotalCents, o.CreatedAt, o.UpdatedAt, o.Items.Count)).ToList();
        return (new AdminOrderPage(summaries, page, pageSize, total), null);
    }

    public async Task<OrderResponse?> GetAsync(Guid orderId, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderId, ct);
        return order is null ? null : CheckoutService.ToResponse(order);
    }

    public async Task<ChangeStatusResult> ChangeStatusAsync(Guid orderId, string? requested, CancellationToken ct)
    {
        if (!TryParseStatus(requested, out var to) || to is not (OrderStatus.Entregado or OrderStatus.Cancelado))
            return new(ChangeStatusOutcome.InvalidStatus, Detail: "El estado debe ser Entregado o Cancelado.");

        var order = await orders.FindAsync(orderId, ct);
        if (order is null) return new(ChangeStatusOutcome.NotFound);
        if (order.Status != OrderStatus.Confirmado)
            return new(ChangeStatusOutcome.InvalidTransition,
                Detail: $"El pedido esta {order.Status}; solo un pedido Confirmado puede pasar a {to}.");

        if (to == OrderStatus.Cancelado)
        {
            try { await stock.ReleaseAsync(order.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "No se pudo liberar el stock al cancelar el pedido {OrderId}", order.Id);
                return new(ChangeStatusOutcome.StockReleaseFailed, Detail: "No se pudo liberar el stock, el pedido no se cancelo. Reintenta.");
            }
        }

        order.TryTransition(to);
        await orders.SaveAsync(ct);
        return new(ChangeStatusOutcome.Changed, CheckoutService.ToResponse(order));
    }
}
