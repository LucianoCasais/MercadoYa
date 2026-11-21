using Microsoft.EntityFrameworkCore;

namespace MercadoYa.Payments;

/// Valores de PaymentRecord.Status (los mismos que el CHECK de infra/postgres/init/04-payments-schema.sql).
public static class PaymentStatuses
{
    public const string Aprobado = "Aprobado";
    public const string Rechazado = "Rechazado";
}

/// Un cobro por pedido. La unicidad de OrderId en la base es lo que hace idempotente al servicio.
public sealed class PaymentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public long AmountCents { get; set; }
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// BD propia del servicio de pagos (database per service): nadie mas lee ni escribe esta base.
/// El esquema lo crea 04-payments-schema.sql; este contexto solo lo mapea (sin migraciones).
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();

    protected override void OnModelCreating(ModelBuilder m) =>
        m.Entity<PaymentRecord>(e =>
        {
            e.ToTable("PaymentRecord");
            e.HasIndex(x => x.OrderId).IsUnique();
            e.Property(x => x.Status).HasMaxLength(10);
            e.Property(x => x.Reason).HasMaxLength(40);
        });
}
