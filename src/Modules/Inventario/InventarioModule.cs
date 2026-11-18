using MercadoYa.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MercadoYa.Modules.Inventario;

public sealed class StockMovement
{
    public long Id { get; set; } public int ProductId { get; set; } public int Delta { get; set; }
    public string Reason { get; set; } = ""; public Guid? OrderId { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class StockReservation
{
    public Guid OrderId { get; set; } public int ProductId { get; set; } public int Quantity { get; set; }
    public string Status { get; set; } = "Reservada"; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class InventarioDbContext(DbContextOptions<InventarioDbContext> options) : DbContext(options)
{
    public DbSet<StockMovement> Movements => Set<StockMovement>();
    public DbSet<StockReservation> Reservations => Set<StockReservation>();
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.HasDefaultSchema("catalogo");   // comparte esquema con Catalogo, pero cada modulo tiene su DbContext
        m.Entity<StockMovement>(e => { e.ToTable("StockMovement"); e.Property(x => x.Reason).HasMaxLength(20); });
        m.Entity<StockReservation>(e => { e.ToTable("StockReservation"); e.HasKey(x => new { x.OrderId, x.ProductId }); });
    }
}

public sealed record StockItem(int ProductId, int Quantity);
public sealed record ReserveRequest(Guid OrderId, IReadOnlyList<StockItem> Items);
public enum ReserveResult { Reserved, AlreadyReserved, InsufficientStock }

/// Reserva atomica e idempotente: UPDATE ... SET Stock = Stock - n WHERE Stock >= n, todos los items en una transaccion.
public sealed class StockService(InventarioDbContext db)
{
    public async Task<ReserveResult> ReserveAsync(Guid orderId, IReadOnlyList<StockItem> items, CancellationToken ct)
    {
        if (await db.Reservations.AnyAsync(r => r.OrderId == orderId, ct)) return ReserveResult.AlreadyReserved;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var i in items.OrderBy(x => x.ProductId))   // orden fijo: evita deadlocks entre compras
            {
                var rows = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""UPDATE catalogo."Product" SET "Stock" = "Stock" - {i.Quantity} WHERE "Id" = {i.ProductId} AND "Active" AND "Stock" >= {i.Quantity}""", ct);
                if (rows == 0) { await tx.RollbackAsync(ct); return ReserveResult.InsufficientStock; }
                db.Reservations.Add(new StockReservation { OrderId = orderId, ProductId = i.ProductId, Quantity = i.Quantity });
                db.Movements.Add(new StockMovement { ProductId = i.ProductId, Delta = -i.Quantity, Reason = "Venta", OrderId = orderId });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ReserveResult.Reserved;
        }
        catch (DbUpdateException)   // reserva concurrente del mismo pedido (PK OrderId+ProductId): ya estaba reservado
        {
            return ReserveResult.AlreadyReserved;
        }
    }

    /// Libera lo reservado por el pedido. Idempotente: solo actua sobre reservas en estado Reservada.
    public async Task ReleaseAsync(Guid orderId, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reservations = await db.Reservations.Where(r => r.OrderId == orderId && r.Status == "Reservada").ToListAsync(ct);
        foreach (var r in reservations)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalogo."Product" SET "Stock" = "Stock" + {r.Quantity} WHERE "Id" = {r.ProductId}""", ct);
            r.Status = "Liberada";
            db.Movements.Add(new StockMovement { ProductId = r.ProductId, Delta = r.Quantity, Reason = reason, OrderId = orderId });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

public static class InventarioModule
{
    public static IServiceCollection AddInventarioModule(this IServiceCollection s, string cs) =>
        s.AddDbContext<InventarioDbContext>(o => o.UseNpgsql(cs)).AddScoped<StockService>();

    /// /internal/v1/stock/**: solo el client de servicio orders-worker (claim azp).
    public static IEndpointRouteBuilder MapInventarioInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/internal/v1/stock").RequireAuthorization(Policies.Worker);
        g.MapPost("/reservations", async (ReserveRequest r, StockService s, CancellationToken ct) =>
            await s.ReserveAsync(r.OrderId, r.Items, ct) == ReserveResult.InsufficientStock
                ? Results.Problem(statusCode: 409, title: "Stock insuficiente") : Results.Ok());
        g.MapPost("/reservations/{orderId:guid}/release", async (Guid orderId, StockService s, CancellationToken ct) =>
        { await s.ReleaseAsync(orderId, "Liberacion", ct); return Results.Ok(); });
        return app;
    }
}
