using System.Collections.Concurrent;
using System.Text;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using MercadoYa.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MercadoYa.Tests;

/// Dias 11 y 12: el worker real (OrderSagaWorker + OrderSagaProcessor) contra un RabbitMQ real, con la topologia real
/// (cola quorum con x-delivery-limit 5 y DLX hacia orders.dead). El stock, el pago y la base son dobles en memoria: aca se prueba
/// la mensajeria (ack, reject, DLQ, consumidores competidores, cortes), no la logica de negocio, que ya cubren los otros tests.
[Collection("rabbitmq")]
[Trait("Category", "Integracion")]
public sealed class OrderSagaRabbitTests(RabbitFixture rabbit)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // ---- dobles de prueba (seguros ante varios workers a la vez) ----

    private sealed class SagaStore : ISagaOrderStore
    {
        public ConcurrentDictionary<Guid, Order> Orders { get; } = new();

        public Order NewOrder(long unitPriceCents = 480000, int quantity = 2)
        {
            var o = new Order { UserId = "u-rabbit", IdempotencyKey = Guid.NewGuid().ToString("N") };
            o.Items.Add(new OrderItem { OrderId = o.Id, ProductId = 1, ProductName = "Yerba", UnitPriceCents = unitPriceCents, Quantity = quantity });
            o.TotalCents = unitPriceCents * quantity;
            Orders[o.Id] = o;
            return o;
        }

        public Task<Order?> FindForSagaAsync(Guid orderId, CancellationToken ct) => Task.FromResult(Orders.GetValueOrDefault(orderId));

        // Igual que el UPDATE ... WHERE Status = 'Pendiente': solo uno gana.
        public Task<bool> TryFinalizeAsync(Guid orderId, OrderStatus to, RejectionReason? reason, CancellationToken ct)
        {
            if (!Orders.TryGetValue(orderId, out var o)) return Task.FromResult(false);
            lock (o) return Task.FromResult(o.TryTransition(to, reason));
        }
    }

    private sealed class Stock : IStockClient
    {
        public ConcurrentDictionary<Guid, int> Reserves { get; } = new();
        public ConcurrentDictionary<Guid, int> Releases { get; } = new();
        public volatile bool ReleaseFails;
        public TaskCompletionSource? ReserveEntered;
        public Task? ReserveBlocker;

        public async Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct)
        {
            Reserves.AddOrUpdate(orderId, 1, (_, n) => n + 1);
            ReserveEntered?.TrySetResult();
            if (ReserveBlocker is not null) await ReserveBlocker.WaitAsync(ct);
            return StockReserveOutcome.Reserved;
        }

        public Task ReleaseAsync(Guid orderId, CancellationToken ct)
        {
            Releases.AddOrUpdate(orderId, 1, (_, n) => n + 1);
            if (ReleaseFails) throw new HttpRequestException("stock caido", null, System.Net.HttpStatusCode.ServiceUnavailable);
            return Task.CompletedTask;
        }
    }

    private sealed class Payments(PaymentOutcome outcome = PaymentOutcome.Approved) : IPaymentGateway
    {
        public Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct) => Task.FromResult(new PaymentResult(outcome));
    }

    /// Uno o mas workers reales (cada uno con su conexion y su consumidor) sobre los mismos dobles.
    private sealed class Workers(IReadOnlyList<IHost> hosts) : IAsyncDisposable
    {
        public static async Task<Workers> StartAsync(RabbitMqOptions options, SagaStore store, Stock stock, IPaymentGateway payments, int count = 1)
        {
            var hosts = new List<IHost>();
            for (var i = 0; i < count; i++)
            {
                var b = Host.CreateApplicationBuilder();
                b.Logging.ClearProviders();
                b.Services.AddSingleton<ISagaOrderStore>(store);
                b.Services.AddSingleton<IStockClient>(stock);
                b.Services.AddSingleton(payments);
                b.Services.AddScoped<OrderSagaProcessor>();
                b.Services.AddHostedService(sp => new OrderSagaWorker(sp.GetRequiredService<IServiceScopeFactory>(), options,
                    sp.GetRequiredService<ILogger<OrderSagaWorker>>(), requeueDelay: TimeSpan.FromMilliseconds(100)));
                var host = b.Build();
                await host.StartAsync();
                hosts.Add(host);
            }
            return new Workers(hosts);
        }

        public Task StopAsync() => Task.WhenAll(hosts.Select(h => h.StopAsync()));

        public async ValueTask DisposeAsync()
        {
            foreach (var h in hosts) { try { await h.StopAsync(); } catch { /* ya detenido */ } h.Dispose(); }
        }
    }

    private async Task PublishAsync(Order order, string correlationId = "test")
    {
        await using var publisher = new RabbitOrderPublisher(rabbit.Options, NullLogger<RabbitOrderPublisher>.Instance);
        await publisher.PublishAsync(order.ToRequestedMessage(), correlationId, CancellationToken.None);
    }

    private static async Task<bool> UntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Wait);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return await condition();
    }

    // ---- pruebas ----

    [Fact]
    public async Task Un_pedido_publicado_con_el_worker_apagado_se_conserva_y_se_confirma_al_arrancar()
    {
        await rabbit.ResetAsync();
        var store = new SagaStore();
        var stock = new Stock();
        var order = store.NewOrder();

        await PublishAsync(order);   // el worker todavia no existe: la cola durable guarda el mensaje
        Assert.Equal(1u, (await rabbit.StatsAsync(RabbitTopology.Queue)).Ready);
        Assert.Equal(OrderStatus.Pendiente, order.Status);

        await using var workers = await Workers.StartAsync(rabbit.Options, store, stock, new Payments());

        Assert.True(await UntilAsync(() => Task.FromResult(order.Status == OrderStatus.Confirmado)));
        Assert.True(await UntilAsync(async () => (await rabbit.StatsAsync(RabbitTopology.Queue)).Ready == 0));   // ack: salio de la cola
        Assert.Equal(0u, (await rabbit.StatsAsync(RabbitTopology.DeadQueue)).Ready);
        Assert.Equal(1, stock.Reserves[order.Id]);
    }

    [Fact]
    public async Task Un_mensaje_ilegible_va_directo_a_la_cola_de_mensajes_muertos()
    {
        await rabbit.ResetAsync();
        await rabbit.PublishRawAsync(Encoding.UTF8.GetBytes("esto no es un OrderRequested"));

        await using var workers = await Workers.StartAsync(rabbit.Options, new SagaStore(), new Stock(), new Payments());

        Assert.True(await UntilAsync(async () => (await rabbit.StatsAsync(RabbitTopology.DeadQueue)).Ready == 1));
        Assert.Equal(0u, (await rabbit.StatsAsync(RabbitTopology.Queue)).Ready);
    }

    [Fact]
    public async Task Si_la_compensacion_nunca_funciona_el_mensaje_se_reintenta_y_termina_en_orders_dead()
    {
        await rabbit.ResetAsync();
        var store = new SagaStore();
        var stock = new Stock { ReleaseFails = true };
        var order = store.NewOrder();
        await PublishAsync(order);

        await using var workers = await Workers.StartAsync(rabbit.Options, store, stock, new Payments(PaymentOutcome.Rejected));

        // Pago rechazado -> el pedido queda Rechazado y hay que liberar el stock, pero la liberacion siempre falla: se devuelve la
        // cola con reject (cuenta para x-delivery-limit) y al superar el limite RabbitMQ lo manda a orders.dead.
        Assert.True(await UntilAsync(async () => (await rabbit.StatsAsync(RabbitTopology.DeadQueue)).Ready == 1));
        Assert.Equal(OrderStatus.Rechazado, order.Status);
        Assert.Equal(RejectionReason.PagoFallido, order.Reason);
        Assert.True(stock.Releases[order.Id] >= 5);   // x-delivery-limit = 5
        Assert.Equal(1, stock.Reserves[order.Id]);    // las reentregas no reservan de nuevo un pedido ya rechazado
        Assert.Equal(0u, (await rabbit.StatsAsync(RabbitTopology.Queue)).Ready);
    }

    [Fact]
    public async Task Dos_workers_reparten_los_pedidos_y_ninguno_se_procesa_dos_veces()
    {
        await rabbit.ResetAsync();
        var store = new SagaStore();
        var stock = new Stock();
        var orders = Enumerable.Range(0, 30).Select(_ => store.NewOrder()).ToList();
        foreach (var o in orders) await PublishAsync(o);

        await using var workers = await Workers.StartAsync(rabbit.Options, store, stock, new Payments(), count: 2);

        Assert.True(await UntilAsync(async () => (await rabbit.StatsAsync(RabbitTopology.Queue)).Consumers == 2));   // dos consumidores competidores sobre la misma cola
        Assert.True(await UntilAsync(() => Task.FromResult(orders.All(o => o.Status == OrderStatus.Confirmado))));
        Assert.All(orders, o => Assert.Equal(1, stock.Reserves[o.Id]));   // cada pedido lo proceso un solo worker, una sola vez
        Assert.Equal(0u, (await rabbit.StatsAsync(RabbitTopology.DeadQueue)).Ready);
    }

    [Fact]
    public async Task Si_el_worker_se_apaga_a_mitad_de_un_pedido_el_mensaje_no_se_pierde()
    {
        await rabbit.ResetAsync();
        var store = new SagaStore();
        var blocker = new TaskCompletionSource();
        var stock = new Stock { ReserveEntered = new TaskCompletionSource(), ReserveBlocker = blocker.Task };   // el stock "se cuelga"
        var order = store.NewOrder();
        await PublishAsync(order);
        var workers = await Workers.StartAsync(rabbit.Options, store, stock, new Payments());
        await stock.ReserveEntered!.Task.WaitAsync(Wait);   // el worker ya tomo el mensaje y esta reservando

        await workers.StopAsync();   // el worker cae antes de poder confirmar el mensaje (ack)
        await workers.DisposeAsync();

        Assert.Equal(OrderStatus.Pendiente, order.Status);   // no llego a nada final
        Assert.True(await UntilAsync(async () => (await rabbit.StatsAsync(RabbitTopology.Queue)).Ready == 1));   // RabbitMQ lo devolvio a la cola
        Assert.Equal(0u, (await rabbit.StatsAsync(RabbitTopology.DeadQueue)).Ready);

        // Otro worker (la replica) lo termina.
        stock.ReserveBlocker = null;
        await using var replacement = await Workers.StartAsync(rabbit.Options, store, stock, new Payments());
        Assert.True(await UntilAsync(() => Task.FromResult(order.Status == OrderStatus.Confirmado)));
    }

    [Fact]
    public async Task Con_RabbitMQ_caido_el_publisher_falla_enseguida_y_no_queda_nada_publicado()
    {
        await rabbit.ResetAsync();
        var unreachable = rabbit.Options with { Port = 1 };   // nadie escucha ahi: es como tener el broker apagado
        await using var publisher = new RabbitOrderPublisher(unreachable, NullLogger<RabbitOrderPublisher>.Instance);
        var order = new SagaStore().NewOrder();
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(order.ToRequestedMessage(), "test", CancellationToken.None));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));   // el timeout de la publicacion es de 5 s: no cuelga la request
        Assert.Equal(0u, (await rabbit.StatsAsync(RabbitTopology.Queue)).Ready);
    }
}
