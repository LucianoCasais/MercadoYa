using System.Text.Json;
using MercadoYa.Shared;
using Microsoft.Extensions.Logging;

namespace MercadoYa.Modules.Pedidos;

/// Que hacer con el mensaje de RabbitMQ despues de procesarlo.
///  Ack        -> terminado (con exito, o ya estaba hecho): se confirma y sale de la cola.
///  Requeue    -> fallo transitorio (BD caida, no se pudo compensar): se devuelve a la cola. La cola `orders` es quorum con
///                x-delivery-limit 5: pasadas 5 entregas RabbitMQ lo manda solo al DLX `orders.dlx` -> cola `orders.dead`.
///  DeadLetter -> mensaje que nunca se va a poder procesar (JSON roto, pedido inexistente): directo a `orders.dead`, sin reintentos.
public enum MessageDisposition { Ack, Requeue, DeadLetter }

/// Puerto de persistencia que usa la SAGA (lo implementa EfOrderRepository).
public interface ISagaOrderStore
{
    /// Pedido con sus items (sin tracking); null si no existe.
    Task<Order?> FindForSagaAsync(Guid orderId, CancellationToken ct);

    /// Cambia el estado SOLO si el pedido sigue Pendiente (UPDATE ... WHERE Status = 'Pendiente'). Devuelve false si otro
    /// proceso ya lo habia finalizado. Es lo que hace segura la SAGA ante mensajes duplicados o dos workers a la vez.
    Task<bool> TryFinalizeAsync(Guid orderId, OrderStatus to, RejectionReason? reason, CancellationToken ct);
}

/// SAGA del checkout (orquestada por el worker). Se ejecuta una vez por mensaje OrderRequested:
///   1. Lee el pedido de la BD (los items y el total NO se toman del mensaje: la BD es la fuente de verdad).
///      - ya Confirmado/Entregado/Cancelado -> mensaje duplicado, ack.
///      - ya Rechazado -> reentrega: se repite la compensacion (liberar stock, idempotente) por si la anterior fallo, y ack.
///   2. Reserva stock (idempotente por pedido): sin stock -> Rechazado(SinStock); error -> Rechazado(StockNoDisponible).
///   3. Cobra (Payments, idempotente por pedido): Aprobado -> Confirmado; Rechazado -> libera stock + Rechazado(PagoFallido);
///      caido o lento -> libera stock + Rechazado(PagoNoDisponible).
/// Al rechazar se finaliza el pedido PRIMERO y despues se libera el stock: si el worker muere o la liberacion falla, el mensaje se
/// reentrega, ve el pedido Rechazado y repite la liberacion. Nunca se reserva de nuevo un pedido ya rechazado.
/// Los cambios de estado son condicionales (solo desde Pendiente): de dos workers que procesen el mismo pedido, gana uno.
public sealed class OrderSagaProcessor(ISagaOrderStore orders, IStockClient stock, IPaymentGateway payments, ILogger<OrderSagaProcessor> logger)
{
    public async Task<MessageDisposition> HandleAsync(ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        OrderRequested? message;
        try { message = OrderMessages.Parse(body.Span); }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Mensaje OrderRequested ilegible; va a la cola de mensajes muertos");
            return MessageDisposition.DeadLetter;
        }
        if (message is null || message.OrderId == Guid.Empty)
        {
            logger.LogError("Mensaje OrderRequested sin OrderId; va a la cola de mensajes muertos");
            return MessageDisposition.DeadLetter;
        }

        try { return await ProcessAsync(message.OrderId, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Error procesando el pedido {OrderId}; se devuelve a la cola para reintentar", message.OrderId);
            return MessageDisposition.Requeue;
        }
    }

    private async Task<MessageDisposition> ProcessAsync(Guid orderId, CancellationToken ct)
    {
        var order = await orders.FindForSagaAsync(orderId, ct);
        if (order is null)
        {
            logger.LogError("El pedido {OrderId} no existe; el mensaje va a la cola de mensajes muertos", orderId);
            return MessageDisposition.DeadLetter;
        }

        switch (order.Status)
        {
            case OrderStatus.Pendiente:
                break;
            case OrderStatus.Rechazado:
                return await CompensateAsync(order, ct);   // reentrega: asegura que el stock quedo liberado
            default:
                logger.LogInformation("El pedido {OrderId} ya esta {Status}; mensaje duplicado, se ignora", orderId, order.Status);
                return MessageDisposition.Ack;
        }

        // Paso 2: reservar stock.
        var lines = order.Items.OrderBy(i => i.ProductId).Select(i => new OrderLineRequest(i.ProductId, i.Quantity)).ToList();
        var reserve = await ReserveAsync(orderId, lines, ct);
        if (reserve == StockReserveOutcome.InsufficientStock)
            return await RejectAsync(orderId, RejectionReason.SinStock, releaseStock: false, ct);   // la reserva es atomica: no quedo nada reservado
        if (reserve == StockReserveOutcome.Unavailable)
            return await RejectAsync(orderId, RejectionReason.StockNoDisponible, releaseStock: true, ct);   // por si la reserva si se aplico

        // Paso 3: cobrar.
        var payment = await ChargeAsync(orderId, order.TotalCents, ct);
        switch (payment.Outcome)
        {
            case PaymentOutcome.Approved:
                if (await orders.TryFinalizeAsync(orderId, OrderStatus.Confirmado, null, ct))
                    logger.LogInformation("Pedido {OrderId} confirmado", orderId);
                else
                    logger.LogInformation("El pedido {OrderId} ya lo habia finalizado otro proceso", orderId);
                return MessageDisposition.Ack;
            case PaymentOutcome.Rejected:
                return await RejectAsync(orderId, RejectionReason.PagoFallido, releaseStock: true, ct);
            default:
                return await RejectAsync(orderId, RejectionReason.PagoNoDisponible, releaseStock: true, ct);
        }
    }

    /// Finaliza el pedido como Rechazado y despues compensa. Solo compensa quien consiguio finalizarlo: si otro proceso ya lo
    /// habia finalizado (quiza como Confirmado) liberar stock seria un error.
    private async Task<MessageDisposition> RejectAsync(Guid orderId, RejectionReason reason, bool releaseStock, CancellationToken ct)
    {
        if (!await orders.TryFinalizeAsync(orderId, OrderStatus.Rechazado, reason, ct))
        {
            logger.LogInformation("El pedido {OrderId} ya lo habia finalizado otro proceso", orderId);
            return MessageDisposition.Ack;
        }
        logger.LogInformation("Pedido {OrderId} rechazado: {Reason}", orderId, reason);
        return releaseStock ? await ReleaseAsync(orderId, ct) : MessageDisposition.Ack;
    }

    /// Reentrega de un pedido ya Rechazado: solo hay que liberar si el rechazo ocurrio despues de reservar.
    private Task<MessageDisposition> CompensateAsync(Order order, CancellationToken ct)
    {
        var reserved = order.Reason is RejectionReason.StockNoDisponible or RejectionReason.PagoFallido or RejectionReason.PagoNoDisponible;
        return reserved ? ReleaseAsync(order.Id, ct) : Task.FromResult(MessageDisposition.Ack);
    }

    private async Task<MessageDisposition> ReleaseAsync(Guid orderId, CancellationToken ct)
    {
        try
        {
            await stock.ReleaseAsync(orderId, ct);
            return MessageDisposition.Ack;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Compensacion fallida: no se confirma el mensaje. Se reintenta hasta x-delivery-limit y despues va a orders.dead.
            logger.LogError(ex, "No se pudo liberar el stock del pedido {OrderId}; se reintenta", orderId);
            return MessageDisposition.Requeue;
        }
    }

    private async Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> lines, CancellationToken ct)
    {
        try { return await stock.ReserveAsync(orderId, lines, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)   // incluye el timeout del HttpClient (TaskCanceledException)
        {
            logger.LogError(ex, "Fallo la reserva de stock del pedido {OrderId}", orderId);
            return StockReserveOutcome.Unavailable;
        }
    }

    private async Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct)
    {
        try { return await payments.ChargeAsync(orderId, amountCents, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Fallo el cobro del pedido {OrderId}", orderId);
            return new PaymentResult(PaymentOutcome.Unavailable, "Servicio de pagos no disponible");
        }
    }
}
