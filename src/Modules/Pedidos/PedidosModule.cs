using MercadoYa.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MercadoYa.Modules.Pedidos;

public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public OrderStatus Status { get; private set; } = OrderStatus.Pendiente;
    public RejectionReason? Reason { get; private set; }
    public long TotalCents { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<OrderItem> Items { get; set; } = [];

    /// Maquina de estados: Pendiente -> Confirmado | Rechazado; Confirmado -> Entregado | Cancelado.
    public bool TryTransition(OrderStatus to, RejectionReason? reason = null)
    {
        var allowed = (Status, to) is (OrderStatus.Pendiente, OrderStatus.Confirmado) or (OrderStatus.Pendiente, OrderStatus.Rechazado)
            or (OrderStatus.Confirmado, OrderStatus.Entregado) or (OrderStatus.Confirmado, OrderStatus.Cancelado);
        if (!allowed || (to == OrderStatus.Rechazado) != reason.HasValue) return false;
        Status = to; Reason = reason; UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }
}

public sealed class OrderItem
{
    public Guid OrderId { get; set; } public int ProductId { get; set; }
    public string ProductName { get; set; } = ""; public long UnitPriceCents { get; set; } public int Quantity { get; set; }
}

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
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.OrderId);
        });
        m.Entity<OrderItem>(e => { e.ToTable("OrderItem"); e.HasKey(x => new { x.OrderId, x.ProductId }); });
    }
}

public static class PedidosModule
{
    public static IServiceCollection AddPedidosModule(this IServiceCollection s, string cs) =>
        s.AddDbContext<PedidosDbContext>(o => o.UseNpgsql(cs));

    /// TODO (dias 3-5 y 9): POST /orders (202, Idempotency-Key, publica OrderRequested), GET /orders/{id} (userId = claim sub),
    /// GET /admin/orders y PATCH /admin/orders/{id}/status (Admin; Cancelado libera stock).
    public static IEndpointRouteBuilder MapPedidosEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapPost("/orders", () => Results.StatusCode(StatusCodes.Status501NotImplemented)).RequireAuthorization(Policies.Cliente);
        return v1;
    }
}
