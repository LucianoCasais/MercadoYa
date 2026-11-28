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

/// PATCH /api/v1/admin/products/{id}/stock. Delta positivo = ingreso de mercaderia, negativo = baja (rotura, conteo, etc.).
/// Se usa delta y no un valor absoluto para no pisar ventas concurrentes.
public sealed record StockAdjustRequest(int Delta);
public sealed record StockAdjustedDto(int ProductId, int Stock);
public enum AdjustOutcome { Adjusted, NotFound, WouldBeNegative }
public sealed record AdjustResult(AdjustOutcome Outcome, int Stock = 0);

/// Reserva atomica e idempotente: UPDATE ... SET Stock = Stock - n WHERE Stock >= n, todos los items en una transaccion.
/// Cada cambio de stock confirmado avisa al catalogo (ICatalogChangeNotifier) para que su cache no muestre stock viejo.
/// El aviso es opcional: sin el (tests de este servicio) el stock se maneja igual.
public sealed class StockService(InventarioDbContext db, ICatalogChangeNotifier? catalog = null)
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
                if (rows == 0)
                {
                    await tx.RollbackAsync(ct);
                    // Si otra request del MISMO pedido gano la carrera y ya reservo (reentrega o doble envio), no es falta de stock:
                    // el stock que no alcanza es el que ella ya descontó. Se informa como ya reservado (idempotente).
                    return await db.Reservations.AnyAsync(r => r.OrderId == orderId, ct) ? ReserveResult.AlreadyReserved : ReserveResult.InsufficientStock;
                }
                db.Reservations.Add(new StockReservation { OrderId = orderId, ProductId = i.ProductId, Quantity = i.Quantity });
                db.Movements.Add(new StockMovement { ProductId = i.ProductId, Delta = -i.Quantity, Reason = "Venta", OrderId = orderId });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            catalog?.CatalogChanged();
            return ReserveResult.Reserved;
        }
        catch (DbUpdateException)   // reserva concurrente del mismo pedido (PK OrderId+ProductId): ya estaba reservado
        {
            return ReserveResult.AlreadyReserved;
        }
    }

    /// Libera lo reservado por el pedido. Idempotente Y seguro ante llamadas simultaneas (por ejemplo la cancelacion del admin
    /// junto a la compensacion del worker): una sola sentencia marca las reservas Reservada -> Liberada y devuelve el stock solo
    /// de las filas que ella misma cambio, asi que la segunda llamada no encuentra nada que liberar. Leer y despues escribir
    /// desde EF permitia que dos llamadas leyeran "Reservada" a la vez y devolvieran el stock dos veces.
    public async Task ReleaseAsync(Guid orderId, string reason, CancellationToken ct)
    {
        var released = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH released AS (
                UPDATE catalogo."StockReservation" SET "Status" = 'Liberada'
                WHERE "OrderId" = {orderId} AND "Status" = 'Reservada'
                RETURNING "ProductId", "Quantity"),
            restocked AS (
                UPDATE catalogo."Product" p SET "Stock" = p."Stock" + r."Quantity"
                FROM released r WHERE p."Id" = r."ProductId"
                RETURNING p."Id")
            INSERT INTO catalogo."StockMovement" ("ProductId", "Delta", "Reason", "OrderId")
            SELECT "ProductId", "Quantity", {reason}, {orderId} FROM released
            """, ct);
        if (released > 0) catalog?.CatalogChanged();   // filas = movimientos insertados = productos que recuperaron stock
    }

    public const int MaxAdjustDelta = 100_000;

    /// Ajuste manual del admin: UPDATE atomico (nunca deja stock negativo) + StockMovement 'Ajuste' en la misma transaccion.
    public async Task<AdjustResult> AdjustAsync(int productId, int delta, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Database.SqlQuery<int>(
            $"""UPDATE catalogo."Product" SET "Stock" = "Stock" + {delta} WHERE "Id" = {productId} AND "Stock" + {delta} >= 0 RETURNING "Stock" AS "Value" """)
            .ToListAsync(ct);
        if (updated.Count == 0)
        {
            var exists = (await db.Database.SqlQuery<int>($"""SELECT 1 AS "Value" FROM catalogo."Product" WHERE "Id" = {productId}""").ToListAsync(ct)).Count > 0;
            return new AdjustResult(exists ? AdjustOutcome.WouldBeNegative : AdjustOutcome.NotFound);
        }
        db.Movements.Add(new StockMovement { ProductId = productId, Delta = delta, Reason = "Ajuste" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        catalog?.CatalogChanged();
        return new AdjustResult(AdjustOutcome.Adjusted, updated[0]);
    }
}

public static class InventarioModule
{
    /// Valida el pedido de reserva. Sin esto, una lista vacia o nula daba 500, y un producto repetido chocaba con la clave
    /// (OrderId, ProductId) de la reserva y se informaba como "ya reservado" (200) sin haber reservado nada.
    public static Dictionary<string, string[]> ValidateReserve(ReserveRequest? r)
    {
        var errors = new Dictionary<string, string[]>();
        if (r is null || r.OrderId == Guid.Empty) errors["orderId"] = ["El pedido es obligatorio."];
        if (r?.Items is not { Count: > 0 } items) { errors["items"] = ["La reserva debe tener al menos un producto."]; return errors; }
        if (items.Any(i => i is null || i.ProductId <= 0 || i.Quantity <= 0))
            errors["items"] = ["Cada producto necesita un id valido y una cantidad mayor a 0."];
        else if (items.Select(i => i.ProductId).Distinct().Count() != items.Count)
            errors["items"] = ["Un producto no puede aparecer dos veces en la misma reserva."];
        return errors;
    }

    public static IServiceCollection AddInventarioModule(this IServiceCollection s, string cs) =>
        s.AddDbContext<InventarioDbContext>(o => o.UseNpgsql(cs)).AddScoped<StockService>();

    /// /internal/v1/stock/**: solo el client de servicio orders-worker (claim azp).
    public static IEndpointRouteBuilder MapInventarioInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/internal/v1/stock").RequireAuthorization(Policies.Worker);
        g.MapPost("/reservations", async (ReserveRequest r, StockService s, CancellationToken ct) =>
        {
            if (ValidateReserve(r) is { Count: > 0 } errors) return Results.ValidationProblem(errors);
            return await s.ReserveAsync(r.OrderId, r.Items, ct) == ReserveResult.InsufficientStock
                ? Results.Problem(statusCode: 409, title: "Stock insuficiente") : Results.Ok();
        }).WithTags("Interno - Stock")
          .WithSummary("Reservar stock de un pedido")
          .WithDescription("Uso interno del worker. Descuenta el stock de todos los ítems en una sola transacción y es idempotente por `orderId`: repetirla responde 200 sin descontar de nuevo. Sin stock suficiente responde 409 y no reserva nada.")
          .Produces(StatusCodes.Status200OK)
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status409Conflict);
        g.MapPost("/reservations/{orderId:guid}/release", async (Guid orderId, StockService s, CancellationToken ct) =>
        { await s.ReleaseAsync(orderId, "Liberacion", ct); return Results.Ok(); })
         .WithTags("Interno - Stock")
         .WithSummary("Liberar el stock de un pedido")
         .WithDescription("Uso interno del worker (compensación de la SAGA). Devuelve al stock lo reservado por el pedido. Es idempotente: repetirla no devuelve el stock dos veces.")
         .Produces(StatusCodes.Status200OK);
        return app;
    }

    /// PATCH /api/v1/admin/products/{id}/stock: solo rol admin.
    public static IEndpointRouteBuilder MapInventarioAdminEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapPatch("/admin/products/{id:int}/stock", async (int id, StockAdjustRequest r, StockService s, CancellationToken ct) =>
        {
            if (r.Delta == 0 || Math.Abs(r.Delta) > StockService.MaxAdjustDelta)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["delta"] = [$"El ajuste debe ser distinto de 0 y de hasta {StockService.MaxAdjustDelta} unidades."] });
            var res = await s.AdjustAsync(id, r.Delta, ct);
            return res.Outcome switch
            {
                AdjustOutcome.Adjusted => Results.Ok(new StockAdjustedDto(id, res.Stock)),
                AdjustOutcome.NotFound => Results.Problem(statusCode: 404, title: "Producto no encontrado", detail: $"No existe el producto {id}."),
                _ => Results.Problem(statusCode: 409, title: "Stock insuficiente", detail: "El ajuste dejaria el stock en negativo."),
            };
        }).RequireAuthorization(Policies.Admin).WithTags("Admin - Productos")
          .WithSummary("Ajustar el stock de un producto")
          .WithDescription("Suma (`delta` positivo) o resta (negativo) unidades y registra un movimiento de stock 'Ajuste'. `delta` no puede ser 0 ni pasar el máximo permitido, y el stock no puede quedar en negativo (409).")
          .Produces<StockAdjustedDto>(StatusCodes.Status200OK)
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status404NotFound)
          .ProducesProblem(StatusCodes.Status409Conflict);
        return v1;
    }
}
