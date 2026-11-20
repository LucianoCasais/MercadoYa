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
public sealed class EfOrderRepository(PedidosDbContext db) : IOrderRepository
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
}

public static class PedidosModule
{
    /// Registra el modulo con los FAKES de stock y pago (dia 3). La API registra el IProductLookup real; los dias 5 y 8
    /// se registran los clientes reales de IStockClient e IPaymentGateway DESPUES de esta llamada (la ultima registracion gana).
    public static IServiceCollection AddPedidosModule(this IServiceCollection s, string cs) =>
        s.AddDbContext<PedidosDbContext>(o => o.UseNpgsql(cs))
         .AddScoped<IOrderRepository, EfOrderRepository>()
         .AddScoped<IStockClient, FakeStockClient>()
         .AddScoped<IPaymentGateway, FakePaymentGateway>()
         .AddScoped<CheckoutService>();

    /// GET /orders/{id} ya esta (adelantado del dia 5 para el seguimiento). TODO (dias 11-12): admin de pedidos. TODO (dia 8): POST /orders pasa a 202 y publica OrderRequested.
    public static IEndpointRouteBuilder MapPedidosEndpoints(this IEndpointRouteBuilder v1) => v1.MapCheckoutEndpoints();
}
