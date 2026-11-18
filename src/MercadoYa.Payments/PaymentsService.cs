using Grpc.Core;
using MercadoYa.Payments.Grpc;
using MercadoYa.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MercadoYa.Payments;

public sealed class PaymentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid OrderId { get; set; } public long AmountCents { get; set; }
    public string Status { get; set; } = ""; public string? Reason { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();
    protected override void OnModelCreating(ModelBuilder m) =>
        m.Entity<PaymentRecord>(e => { e.ToTable("PaymentRecord"); e.HasIndex(x => x.OrderId).IsUnique(); });
}

/// Cobro simulado e idempotente por order_id. Regla de simulacion para pruebas: importes cuyos centavos
/// terminan en 999 (amount_cents % 1000 == 999) se rechazan; el resto se aprueba.
[Authorize(Policy = Policies.Worker)]
public sealed class PaymentsService(PaymentsDbContext db) : Grpc.Payments.PaymentsBase
{
    public override async Task<ChargeOrderReply> ChargeOrder(ChargeOrderRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.OrderId, out var orderId) || request.AmountCents <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "order_id o amount_cents invalidos"));

        var existing = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId, context.CancellationToken);
        if (existing is not null) return ToReply(existing);

        var rejected = request.AmountCents % 1000 == 999;
        var record = new PaymentRecord { OrderId = orderId, AmountCents = request.AmountCents,
            Status = rejected ? "Rechazado" : "Aprobado", Reason = rejected ? "Pago simulado rechazado" : null };
        db.Payments.Add(record);
        try { await db.SaveChangesAsync(context.CancellationToken); }
        catch (DbUpdateException)   // cobro concurrente del mismo pedido: devolver el ya registrado
        {
            record = await db.Payments.AsNoTracking().FirstAsync(p => p.OrderId == orderId, context.CancellationToken);
        }
        return ToReply(record);
    }

    private static ChargeOrderReply ToReply(PaymentRecord p) => new()
    {
        Status = p.Status == "Aprobado" ? PaymentStatus.Aprobado : PaymentStatus.Rechazado,
        Reason = p.Reason ?? ""
    };
}
