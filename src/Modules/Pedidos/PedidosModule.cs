using MercadoYa.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MercadoYa.Modules.Pedidos;

public sealed class PedidosDbContext(DbContextOptions<PedidosDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.HasDefaultSchema("pedidos");
        m.Entity<Order>(e =>
        {
            e.ToTable("Order");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Reason).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.BuyerName).HasMaxLength(BuyerInfo.MaxLength);
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.OrderId);
        });
        m.Entity<OrderItem>(e => { e.ToTable("OrderItem"); e.HasKey(x => new { x.OrderId, x.ProductId }); });
    }
}

/// Persistencia de pedidos con EF Core. La unicidad de (UserId, IdempotencyKey) la garantiza el indice unico de la BD.
public sealed class EfOrderRepository(PedidosDbContext db) : IOrderRepository, IAdminOrderRepository
{
    public Task<Order?> FindByKeyAsync(string userId, string idempotencyKey, CancellationToken ct) =>
        db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.UserId == userId && o.IdempotencyKey == idempotencyKey, ct);

    public Task<Order?> FindByIdAsync(string userId, Guid orderId, CancellationToken ct) =>
        db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct);

    public async Task<bool> TryAddAsync(Order order, CancellationToken ct)
    {
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();   // el pedido no se guardo: se descarta del contexto
            return false;
        }
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // ---- IAdminOrderRepository (panel de admin: pedidos de cualquier usuario) ----
    public async Task<(IReadOnlyList<Order> Items, int Total)> ListAsync(OrderStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var q = db.Orders.AsNoTracking().AsQueryable();
        if (status is { } s) q = q.Where(o => o.Status == s);
        var total = await q.CountAsync(ct);
        var items = await q.Include(o => o.Items).OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public Task<Order?> FindAsync(Guid orderId, CancellationToken ct) =>
        db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
}

public static class PedidosModule
{
    /// Registra el modulo con los FAKES de stock y pago (dia 3). La API registra el IProductLookup real; los dias 5 y 8
    /// se registran los clientes reales de IStockClient e IPaymentGateway DESPUES de esta llamada (la ultima registracion gana).
    public static IServiceCollection AddPedidosModule(this IServiceCollection s, string cs) =>
        s.AddDbContext<PedidosDbContext>(o => o.UseNpgsql(cs))
         .AddScoped<IOrderRepository, EfOrderRepository>()
         .AddScoped<IAdminOrderRepository, EfOrderRepository>()
         .AddScoped<AdminOrderService>()
         .AddScoped<IStockClient, FakeStockClient>()
         .AddScoped<IPaymentGateway, FakePaymentGateway>()
         .AddScoped<CheckoutService>();

    /// GET /orders/{id} ya esta (adelantado del dia 5 para el seguimiento). Admin de pedidos: dia 6. POST /orders: modo Async (dia 8) en Checkout:Mode.
    public static IEndpointRouteBuilder MapPedidosEndpoints(this IEndpointRouteBuilder v1) => v1.MapCheckoutEndpoints().MapAdminOrderEndpoints();
}
