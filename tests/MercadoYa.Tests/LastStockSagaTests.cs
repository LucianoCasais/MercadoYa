using MercadoYa.Modules.Inventario;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MercadoYa.Tests;

/// Dia 12 (pruebas de fallo): la SAGA completa del worker (OrderSagaProcessor) con el stock y los pedidos en PostgreSQL REAL y
/// varios mensajes procesandose a la vez, como con dos replicas del worker. El broker queda afuera: aca se prueba la consistencia
/// del stock cuando todos compiten por lo mismo (reserva atomica, compensacion y idempotencia de punta a punta).
[Collection("postgres")]
[Trait("Category", "Integracion")]
public sealed class LastStockSagaTests(PostgresFixture pg)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// IStockClient sobre el StockService real: un DbContext (y una conexion) por llamada, como cada request del endpoint interno.
    private sealed class DbStockClient(PostgresFixture pg) : IStockClient
    {
        public async Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct)
        {
            await using var db = pg.NewContext();
            var result = await new StockService(db).ReserveAsync(orderId, items.Select(i => new StockItem(i.ProductId, i.Quantity)).ToList(), ct);
            return result == ReserveResult.InsufficientStock ? StockReserveOutcome.InsufficientStock : StockReserveOutcome.Reserved;
        }

        public async Task ReleaseAsync(Guid orderId, CancellationToken ct)
        {
            await using var db = pg.NewContext();
            await new StockService(db).ReleaseAsync(orderId, "Liberacion", ct);
        }
    }

    private sealed class FixedPayments(PaymentOutcome outcome) : IPaymentGateway
    {
        public Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct) => Task.FromResult(new PaymentResult(outcome));
    }

    private PedidosDbContext NewPedidosDb() => new(new DbContextOptionsBuilder<PedidosDbContext>().UseNpgsql(pg.ConnectionString).Options);

    private async Task<Order> NewPendingOrderAsync(int productId, int quantity = 1)
    {
        var order = new Order { UserId = "u-ultimo-stock", IdempotencyKey = Guid.NewGuid().ToString("N"), TotalCents = 100000L * quantity };
        order.Items.Add(new OrderItem { OrderId = order.Id, ProductId = productId, ProductName = "Producto de prueba", UnitPriceCents = 100000, Quantity = quantity });
        await using var db = NewPedidosDb();
        Assert.True(await new EfOrderRepository(db).TryAddAsync(order, Ct));
        return order;
    }

    /// Procesa el mensaje como lo haria un worker: un scope (DbContext) nuevo por mensaje.
    private async Task<MessageDisposition> ProcessAsync(Order order, PaymentOutcome payment = PaymentOutcome.Approved)
    {
        await using var db = NewPedidosDb();
        var processor = new OrderSagaProcessor(new EfOrderRepository(db), new DbStockClient(pg), new FixedPayments(payment), NullLogger<OrderSagaProcessor>.Instance);
        return await processor.HandleAsync(order.ToRequestedMessage().ToBytes(), Ct);
    }

    private async Task<Order> ReadAsync(Guid id)
    {
        await using var db = NewPedidosDb();
        return (await new EfOrderRepository(db).FindForSagaAsync(id, Ct))!;
    }

    /// Lanza todas las tareas en hilos distintos y las libera juntas para que compitan de verdad.
    private static async Task<T[]> RaceAsync<T>(IEnumerable<Func<Task<T>>> work)
    {
        var start = new TaskCompletionSource();
        var tasks = work.Select(w => Task.Run(async () => { await start.Task; return await w(); })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Dos_compras_del_ultimo_stock_una_queda_Confirmada_y_la_otra_Rechazada_por_SinStock()
    {
        var product = await pg.NewProductAsync(stock: 1);
        var a = await NewPendingOrderAsync(product);
        var b = await NewPendingOrderAsync(product);

        var results = await RaceAsync([() => ProcessAsync(a), () => ProcessAsync(b)]);

        Assert.All(results, r => Assert.Equal(MessageDisposition.Ack, r));   // las dos terminaron: ninguna se reintenta
        var states = new[] { await ReadAsync(a.Id), await ReadAsync(b.Id) };
        Assert.Equal(1, states.Count(o => o.Status == OrderStatus.Confirmado));
        var rechazado = Assert.Single(states, o => o.Status == OrderStatus.Rechazado);
        Assert.Equal(RejectionReason.SinStock, rechazado.Reason);
        Assert.Equal(0, await pg.StockOfAsync(product));   // se vendio una unidad, y nunca queda negativo
        Assert.Equal(1, (await pg.MovementsOfAsync(product)).Count(m => m.Reason == "Venta"));
    }

    [Fact]
    public async Task Veinte_pedidos_sobre_cinco_unidades_confirman_exactamente_cinco()
    {
        var product = await pg.NewProductAsync(stock: 5);
        var orders = new List<Order>();
        for (var i = 0; i < 20; i++) orders.Add(await NewPendingOrderAsync(product));

        var results = await RaceAsync(orders.Select<Order, Func<Task<MessageDisposition>>>(o => () => ProcessAsync(o)));

        Assert.All(results, r => Assert.Equal(MessageDisposition.Ack, r));
        var states = new List<Order>();
        foreach (var o in orders) states.Add(await ReadAsync(o.Id));
        Assert.Equal(5, states.Count(o => o.Status == OrderStatus.Confirmado));
        Assert.Equal(15, states.Count(o => o.Status == OrderStatus.Rechazado && o.Reason == RejectionReason.SinStock));
        Assert.Equal(0, await pg.StockOfAsync(product));
    }

    [Fact]
    public async Task El_pago_rechazado_devuelve_el_stock_reservado()
    {
        var product = await pg.NewProductAsync(stock: 3);
        var order = await NewPendingOrderAsync(product, quantity: 2);

        Assert.Equal(MessageDisposition.Ack, await ProcessAsync(order, PaymentOutcome.Rejected));

        var saved = await ReadAsync(order.Id);
        Assert.Equal(OrderStatus.Rechazado, saved.Status);
        Assert.Equal(RejectionReason.PagoFallido, saved.Reason);
        Assert.Equal(3, await pg.StockOfAsync(product));   // se reservaron 2 y se devolvieron
        Assert.Equal("Liberada", Assert.Single(await pg.ReservationsOfAsync(order.Id)).Status);
    }

    [Fact]
    public async Task Con_Payments_caido_el_pedido_se_rechaza_y_el_stock_se_libera()
    {
        var product = await pg.NewProductAsync(stock: 4);
        var order = await NewPendingOrderAsync(product, quantity: 3);

        Assert.Equal(MessageDisposition.Ack, await ProcessAsync(order, PaymentOutcome.Unavailable));

        var saved = await ReadAsync(order.Id);
        Assert.Equal(OrderStatus.Rechazado, saved.Status);
        Assert.Equal(RejectionReason.PagoNoDisponible, saved.Reason);
        Assert.Equal(4, await pg.StockOfAsync(product));
    }

    [Fact]
    public async Task El_mismo_mensaje_procesado_a_la_vez_por_dos_workers_descuenta_el_stock_una_sola_vez()
    {
        var product = await pg.NewProductAsync(stock: 5);
        var order = await NewPendingOrderAsync(product, quantity: 2);

        var results = await RaceAsync([() => ProcessAsync(order), () => ProcessAsync(order)]);

        Assert.All(results, r => Assert.Equal(MessageDisposition.Ack, r));
        Assert.Equal(OrderStatus.Confirmado, (await ReadAsync(order.Id)).Status);
        Assert.Equal(3, await pg.StockOfAsync(product));   // 5 - 2, no 5 - 4
        Assert.Single(await pg.ReservationsOfAsync(order.Id));
    }

    [Fact]
    public async Task Un_mensaje_reentregado_despues_de_confirmar_no_toca_el_stock()
    {
        var product = await pg.NewProductAsync(stock: 2);
        var order = await NewPendingOrderAsync(product);
        await ProcessAsync(order);

        Assert.Equal(MessageDisposition.Ack, await ProcessAsync(order));   // reentrega (por ejemplo el worker murio antes del ack)

        Assert.Equal(OrderStatus.Confirmado, (await ReadAsync(order.Id)).Status);
        Assert.Equal(1, await pg.StockOfAsync(product));
    }
}
