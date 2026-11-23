using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MercadoYa.Tests;

/// Dia 9: persistencia de la SAGA contra PostgreSQL real. Lo importante es TryFinalizeAsync: un UPDATE condicional
/// (WHERE Status = 'Pendiente') que garantiza que, aunque dos workers procesen el mismo pedido, solo uno lo finaliza.
[Collection("postgres")]
[Trait("Category", "Integracion")]
public sealed class OrderSagaStoreTests(PostgresFixture pg)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private PedidosDbContext NewDb() => new(new DbContextOptionsBuilder<PedidosDbContext>().UseNpgsql(pg.ConnectionString).Options);

    private async Task<Guid> NewPendingOrderAsync(int? productId = null, int quantity = 2)
    {
        var order = new Order { UserId = "u-saga", IdempotencyKey = Guid.NewGuid().ToString("N"), TotalCents = 200000 };
        if (productId is { } p)
            order.Items.Add(new OrderItem { OrderId = order.Id, ProductId = p, ProductName = "Producto de prueba", UnitPriceCents = 100000, Quantity = quantity });
        await using var db = NewDb();
        Assert.True(await new EfOrderRepository(db).TryAddAsync(order, Ct));
        return order.Id;
    }

    private async Task<bool> FinalizeAsync(Guid id, OrderStatus to, RejectionReason? reason = null)
    {
        await using var db = NewDb();   // un contexto por llamada, como cada mensaje del worker
        return await new EfOrderRepository(db).TryFinalizeAsync(id, to, reason, Ct);
    }

    private async Task<Order?> ReadAsync(Guid id)
    {
        await using var db = NewDb();
        return await new EfOrderRepository(db).FindForSagaAsync(id, Ct);
    }

    [Fact]
    public async Task Confirma_un_pedido_pendiente_y_el_segundo_intento_no_cambia_nada()
    {
        var id = await NewPendingOrderAsync();

        Assert.True(await FinalizeAsync(id, OrderStatus.Confirmado));
        Assert.False(await FinalizeAsync(id, OrderStatus.Rechazado, RejectionReason.PagoFallido));

        var order = await ReadAsync(id);
        Assert.Equal(OrderStatus.Confirmado, order!.Status);
        Assert.Null(order.Reason);
    }

    [Fact]
    public async Task Rechaza_un_pedido_pendiente_guardando_el_motivo()
    {
        var id = await NewPendingOrderAsync();

        Assert.True(await FinalizeAsync(id, OrderStatus.Rechazado, RejectionReason.SinStock));

        var order = await ReadAsync(id);
        Assert.Equal(OrderStatus.Rechazado, order!.Status);
        Assert.Equal(RejectionReason.SinStock, order.Reason);
    }

    [Fact]
    public async Task Pedido_inexistente_no_se_puede_finalizar_ni_leer()
    {
        Assert.False(await FinalizeAsync(Guid.NewGuid(), OrderStatus.Confirmado));
        Assert.Null(await ReadAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Dos_workers_a_la_vez_uno_confirma_y_otro_rechaza_solo_uno_gana()
    {
        for (var round = 0; round < 20; round++)
        {
            var id = await NewPendingOrderAsync();
            var start = new TaskCompletionSource();
            var confirm = Task.Run(async () => { await start.Task; return await FinalizeAsync(id, OrderStatus.Confirmado); });
            var reject = Task.Run(async () => { await start.Task; return await FinalizeAsync(id, OrderStatus.Rechazado, RejectionReason.StockNoDisponible); });
            start.SetResult();
            var results = await Task.WhenAll(confirm, reject);

            Assert.Equal(1, results.Count(r => r));   // exactamente uno finalizo el pedido
            var order = await ReadAsync(id);
            // El estado final coincide con el del ganador: nunca queda Confirmado con motivo ni Rechazado sin motivo.
            Assert.Equal(results[0] ? OrderStatus.Confirmado : OrderStatus.Rechazado, order!.Status);
            Assert.Equal(results[0], order.Reason is null);
        }
    }

    [Fact]
    public async Task Lee_el_pedido_con_sus_items_para_la_saga()
    {
        var product = await pg.NewProductAsync(stock: 10);
        var id = await NewPendingOrderAsync(product, quantity: 3);

        var order = await ReadAsync(id);

        Assert.Equal(OrderStatus.Pendiente, order!.Status);
        Assert.Single(order.Items);
        Assert.Equal(product, order.Items[0].ProductId);
        Assert.Equal(3, order.Items[0].Quantity);
        Assert.Equal(200000L, order.TotalCents);
    }
}
