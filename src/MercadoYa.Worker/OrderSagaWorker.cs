namespace MercadoYa.Worker;

/// Orquestador de la SAGA del checkout (ver docs/secuencia-checkout-saga.md). Esqueleto: se implementa los dias 8-10.
///
/// Por cada mensaje OrderRequested de la cola `orders` (prefetch 1, ack manual):
///   1. Leer el pedido; si ya no esta Pendiente -> ack y fin (mensaje duplicado).
///   2. POST /internal/v1/stock/reservations (token client credentials de orders-worker).
///        409 -> Rechazado(SinStock); fallo tras reintentos -> liberar (idempotente) y Rechazado(StockNoDisponible).
///   3. gRPC Payments.ChargeOrder (deadline 5 s, Polly: 3 reintentos con backoff y jitter, circuit breaker 5 fallos / 30 s).
///        Rechazado -> liberar stock y Rechazado(PagoFallido); caido o lento -> liberar y Rechazado(PagoNoDisponible).
///   4. Aprobado -> Confirmado (solo si sigue Pendiente) y ack.
/// Si falla la compensacion: no hacer ack; tras x-delivery-limit el mensaje va al DLX y a la cola `orders.dead`.
public sealed class OrderSagaWorker(ILogger<OrderSagaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OrderSagaWorker iniciado (esqueleto: aun no consume la cola orders)");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
