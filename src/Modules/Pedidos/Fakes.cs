namespace MercadoYa.Modules.Pedidos;

/// FAKE (dia 3): reserva siempre y no toca el stock real. El dia 5 se reemplaza por el cliente real de Inventario.
public sealed class FakeStockClient : IStockClient
{
    public Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct) =>
        Task.FromResult(StockReserveOutcome.Reserved);

    public Task ReleaseAsync(Guid orderId, CancellationToken ct) => Task.CompletedTask;
}

/// FAKE (dia 3): misma regla de simulacion que el servicio Payments (importes que terminan en 999 centavos se rechazan).
/// El dia 8 se reemplaza por el cliente gRPC real.
public sealed class FakePaymentGateway : IPaymentGateway
{
    public Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct) =>
        Task.FromResult(amountCents % 1000 == 999
            ? new PaymentResult(PaymentOutcome.Rejected, "Pago simulado rechazado")
            : new PaymentResult(PaymentOutcome.Approved));
}
