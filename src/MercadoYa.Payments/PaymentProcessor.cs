using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MercadoYa.Payments;

/// Regla del cobro SIMULADO (el acta excluye pasarelas reales). Para poder probar el rechazo desde la tienda:
/// los importes cuyos centavos terminan en 999 se rechazan (por ejemplo un producto de $ 9,99); el resto se aprueba.
public static class PaymentRules
{
    public const string RejectedReason = "Pago simulado rechazado";

    public static bool IsRejected(long amountCents) => amountCents % 1000 == 999;
}

/// Resultado de un cobro. Replayed = true cuando el pedido ya estaba cobrado y se devolvio el resultado guardado.
public sealed record ChargeResult(bool Approved, string? Reason, bool Replayed);

/// El pedido ya tiene un cobro registrado por OTRO importe: no es un reintento sino un error de quien llama.
public sealed class PaymentConflictException(Guid orderId, long registeredCents, long requestedCents)
    : Exception($"El pedido {orderId} ya tiene un cobro por {registeredCents} centavos; no se puede cobrar {requestedCents}.")
{
    public Guid OrderId { get; } = orderId;
}

/// Cobro simulado e idempotente por pedido.
/// - Primer cobro: se decide con PaymentRules y se guarda en PaymentRecord.
/// - Repeticion (reintento del worker, mensaje duplicado): devuelve EXACTAMENTE el resultado guardado, sin cobrar de nuevo.
/// - Dos cobros simultaneos del mismo pedido: el indice unico de OrderId deja pasar uno solo; el otro lee el ganador.
public sealed class PaymentProcessor(PaymentsDbContext db, ILogger<PaymentProcessor> logger)
{
    public async Task<ChargeResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct)
    {
        if (orderId == Guid.Empty) throw new ArgumentException("El pedido es obligatorio.", nameof(orderId));
        if (amountCents <= 0) throw new ArgumentOutOfRangeException(nameof(amountCents), "El importe debe ser mayor a 0.");

        var existing = await FindAsync(orderId, ct);
        if (existing is not null) return Replay(existing, amountCents);

        var rejected = PaymentRules.IsRejected(amountCents);
        var record = new PaymentRecord
        {
            OrderId = orderId,
            AmountCents = amountCents,
            Status = rejected ? PaymentStatuses.Rechazado : PaymentStatuses.Aprobado,
            Reason = rejected ? PaymentRules.RejectedReason : null,
        };
        db.Payments.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Otro cobro del mismo pedido gano la carrera entre la lectura y el INSERT: se devuelve el suyo.
            db.ChangeTracker.Clear();
            var winner = await FindAsync(orderId, ct)
                         ?? throw new InvalidOperationException($"El cobro del pedido {orderId} choco con otro y no aparece en la base.");
            return Replay(winner, amountCents);
        }

        logger.LogInformation("Cobro {Status} del pedido {OrderId} por {AmountCents} centavos", record.Status, orderId, amountCents);
        return new ChargeResult(!rejected, record.Reason, Replayed: false);
    }

    private Task<PaymentRecord?> FindAsync(Guid orderId, CancellationToken ct) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId, ct);

    private ChargeResult Replay(PaymentRecord p, long requestedCents)
    {
        if (p.AmountCents != requestedCents) throw new PaymentConflictException(p.OrderId, p.AmountCents, requestedCents);
        logger.LogInformation("Cobro repetido del pedido {OrderId}: se devuelve el resultado guardado ({Status})", p.OrderId, p.Status);
        return new ChargeResult(p.Status == PaymentStatuses.Aprobado, p.Reason, Replayed: true);
    }
}
