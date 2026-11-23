using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MercadoYa.Tests;

public class AdminOrderServiceTests
{
    // ---- dobles de prueba (sin base de datos) ----
    private sealed class InMemoryAdminOrders(params Order[] orders) : IAdminOrderRepository
    {
        public int Saves { get; private set; }
        public Task<(IReadOnlyList<Order> Items, int Total)> ListAsync(OrderStatus? status, int page, int pageSize, CancellationToken ct)
        {
            var q = orders.Where(o => status is null || o.Status == status).OrderByDescending(o => o.CreatedAt).ToList();
            return Task.FromResult<(IReadOnlyList<Order>, int)>((q.Skip((page - 1) * pageSize).Take(pageSize).ToList(), q.Count));
        }
        public Task<Order?> FindAsync(Guid orderId, CancellationToken ct) => Task.FromResult(orders.FirstOrDefault(o => o.Id == orderId));
        public Task SaveAsync(CancellationToken ct) { Saves++; return Task.CompletedTask; }
    }

    private sealed class ScriptedStock(bool failRelease = false) : IStockClient
    {
        public int Releases { get; private set; }
        public Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct) =>
            Task.FromResult(StockReserveOutcome.Reserved);
        public Task ReleaseAsync(Guid orderId, CancellationToken ct)
        {
            Releases++;
            if (failRelease) throw new HttpRequestException("stock caido");
            return Task.CompletedTask;
        }
    }

    private static Order NewOrder(OrderStatus status = OrderStatus.Confirmado, int minutesAgo = 0)
    {
        var o = new Order
        {
            UserId = "u1", BuyerName = "Cliente Prueba", TotalCents = 1000000, IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            Items = [new OrderItem { ProductId = 1, ProductName = "Yerba", UnitPriceCents = 500000, Quantity = 2 }],
        };
        if (status != OrderStatus.Pendiente)
        {
            Assert.True(o.TryTransition(status == OrderStatus.Rechazado ? OrderStatus.Rechazado : OrderStatus.Confirmado,
                status == OrderStatus.Rechazado ? RejectionReason.SinStock : null));
            if (status is OrderStatus.Entregado or OrderStatus.Cancelado) Assert.True(o.TryTransition(status));
        }
        return o;
    }

    private static AdminOrderService Svc(InMemoryAdminOrders repo, ScriptedStock stock) =>
        new(repo, stock, NullLogger<AdminOrderService>.Instance);

    // ---- cambio de estado ----
    [Fact]
    public async Task Confirmado_a_Entregado_cambia_estado_y_no_toca_el_stock()
    {
        var o = NewOrder(); var repo = new InMemoryAdminOrders(o); var stock = new ScriptedStock();
        var r = await Svc(repo, stock).ChangeStatusAsync(o.Id, "Entregado", default);
        Assert.Equal(ChangeStatusOutcome.Changed, r.Outcome);
        Assert.Equal("Entregado", r.Order!.Status);
        Assert.Equal(OrderStatus.Entregado, o.Status);
        Assert.Equal(0, stock.Releases);
        Assert.Equal(1, repo.Saves);
    }

    [Fact]
    public async Task Confirmado_a_Cancelado_libera_el_stock_una_vez()
    {
        var o = NewOrder(); var repo = new InMemoryAdminOrders(o); var stock = new ScriptedStock();
        var r = await Svc(repo, stock).ChangeStatusAsync(o.Id, "cancelado", default);   // sin distinguir mayusculas
        Assert.Equal(ChangeStatusOutcome.Changed, r.Outcome);
        Assert.Equal(OrderStatus.Cancelado, o.Status);
        Assert.Equal(1, stock.Releases);
        Assert.Equal(1, repo.Saves);
    }

    [Theory]
    [InlineData(OrderStatus.Pendiente)]
    [InlineData(OrderStatus.Rechazado)]
    [InlineData(OrderStatus.Entregado)]
    [InlineData(OrderStatus.Cancelado)]
    public async Task Solo_un_pedido_Confirmado_puede_cambiar(OrderStatus current)
    {
        var o = NewOrder(current); var repo = new InMemoryAdminOrders(o); var stock = new ScriptedStock();
        var r = await Svc(repo, stock).ChangeStatusAsync(o.Id, "Cancelado", default);
        Assert.Equal(ChangeStatusOutcome.InvalidTransition, r.Outcome);
        Assert.Equal(current, o.Status);
        Assert.Equal(0, stock.Releases);   // cancelar dos veces no libera dos veces
        Assert.Equal(0, repo.Saves);
    }

    [Theory]
    [InlineData("Confirmado")]
    [InlineData("Pendiente")]
    [InlineData("Rechazado")]
    [InlineData("inventado")]
    [InlineData("3")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Solo_se_aceptan_Entregado_o_Cancelado(string? requested)
    {
        var o = NewOrder(); var repo = new InMemoryAdminOrders(o); var stock = new ScriptedStock();
        var r = await Svc(repo, stock).ChangeStatusAsync(o.Id, requested, default);
        Assert.Equal(ChangeStatusOutcome.InvalidStatus, r.Outcome);
        Assert.Equal(OrderStatus.Confirmado, o.Status);
        Assert.Equal(0, stock.Releases);
    }

    [Fact]
    public async Task Pedido_inexistente_devuelve_NotFound()
    {
        var r = await Svc(new InMemoryAdminOrders(NewOrder()), new ScriptedStock()).ChangeStatusAsync(Guid.NewGuid(), "Entregado", default);
        Assert.Equal(ChangeStatusOutcome.NotFound, r.Outcome);
    }

    [Fact]
    public async Task Si_falla_la_liberacion_el_pedido_sigue_Confirmado_y_se_puede_reintentar()
    {
        var o = NewOrder(); var repo = new InMemoryAdminOrders(o);
        var r = await Svc(repo, new ScriptedStock(failRelease: true)).ChangeStatusAsync(o.Id, "Cancelado", default);
        Assert.Equal(ChangeStatusOutcome.StockReleaseFailed, r.Outcome);
        Assert.Equal(OrderStatus.Confirmado, o.Status);
        Assert.Equal(0, repo.Saves);

        var retry = await Svc(repo, new ScriptedStock()).ChangeStatusAsync(o.Id, "Cancelado", default);
        Assert.Equal(ChangeStatusOutcome.Changed, retry.Outcome);
        Assert.Equal(OrderStatus.Cancelado, o.Status);
    }

    // ---- listado ----
    [Fact]
    public async Task Listado_filtra_por_estado_y_ordena_mas_nuevos_primero()
    {
        var viejo = NewOrder(OrderStatus.Confirmado, minutesAgo: 10);
        var nuevo = NewOrder(OrderStatus.Confirmado, minutesAgo: 1);
        var otro = NewOrder(OrderStatus.Rechazado, minutesAgo: 5);
        var svc = Svc(new InMemoryAdminOrders(viejo, otro, nuevo), new ScriptedStock());

        var (all, _) = await svc.ListAsync(null, 1, 20, default);
        Assert.Equal(3, all!.Total);
        Assert.Equal(nuevo.Id, all.Items[0].Id);

        var (conf, _) = await svc.ListAsync("confirmado", 1, 20, default);
        Assert.Equal(2, conf!.Total);
        Assert.Equal(1, conf.Items[0].ItemCount);   // ItemCount cuenta renglones (1 renglon de 2 unidades)
    }

    [Fact]
    public async Task Listado_con_estado_invalido_devuelve_error()
    {
        var (page, error) = await Svc(new InMemoryAdminOrders(), new ScriptedStock()).ListAsync("xx", 1, 20, default);
        Assert.Null(page);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Listado_pagina_y_corrige_parametros_invalidos()
    {
        var orders = Enumerable.Range(0, 5).Select(i => NewOrder(minutesAgo: i)).ToArray();
        var svc = Svc(new InMemoryAdminOrders(orders), new ScriptedStock());

        var (p2, _) = await svc.ListAsync(null, 2, 2, default);
        Assert.Equal(2, p2!.Items.Count);
        Assert.Equal(5, p2.Total);
        Assert.Equal(orders[2].Id, p2.Items[0].Id);

        var (bad, _) = await svc.ListAsync(null, 0, 1000, default);
        Assert.Equal(1, bad!.Page);
        Assert.Equal(AdminOrderService.DefaultPageSize, bad.PageSize);
    }

    [Fact]
    public async Task Detalle_devuelve_el_pedido_o_null()
    {
        var o = NewOrder(); var svc = Svc(new InMemoryAdminOrders(o), new ScriptedStock());
        Assert.Equal(o.Id, (await svc.GetAsync(o.Id, default))!.Id);
        Assert.Null(await svc.GetAsync(Guid.NewGuid(), default));
    }
}
