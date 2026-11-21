using System.Text.Json;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MercadoYa.Tests;

/// Dia 8: checkout asincrono (Checkout:Mode = Async). La API crea el pedido Pendiente, publica OrderRequested y responde sin
/// esperar a la SAGA; si RabbitMQ no confirma el mensaje, el pedido se rechaza (BrokerNoDisponible). Sin RabbitMQ real.
public class CheckoutAsyncTests
{
    // ---- dobles de prueba ----
    private sealed class InMemoryOrders : IOrderRepository
    {
        public List<Order> Saved { get; } = [];
        public Task<Order?> FindByKeyAsync(string userId, string key, CancellationToken ct) =>
            Task.FromResult(Saved.FirstOrDefault(o => o.UserId == userId && o.IdempotencyKey == key));
        public Task<Order?> FindByIdAsync(string userId, Guid orderId, CancellationToken ct) =>
            Task.FromResult(Saved.FirstOrDefault(o => o.Id == orderId && o.UserId == userId));
        public Task<bool> TryAddAsync(Order order, CancellationToken ct)
        {
            if (Saved.Any(o => o.UserId == order.UserId && o.IdempotencyKey == order.IdempotencyKey)) return Task.FromResult(false);
            Saved.Add(order);
            return Task.FromResult(true);
        }
        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubProducts(params ProductInfo[] items) : IProductLookup
    {
        public Task<IReadOnlyDictionary<int, ProductInfo>> GetAsync(IReadOnlyCollection<int> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<int, ProductInfo>>(items.Where(p => ids.Contains(p.Id)).ToDictionary(p => p.Id));
    }

    private sealed class CountingStock : IStockClient
    {
        public int Calls { get; private set; }
        public Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct) { Calls++; return Task.FromResult(StockReserveOutcome.Reserved); }
        public Task ReleaseAsync(Guid orderId, CancellationToken ct) { Calls++; return Task.CompletedTask; }
    }

    private sealed class CountingPayments : IPaymentGateway
    {
        public int Calls { get; private set; }
        public Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct) { Calls++; return Task.FromResult(new PaymentResult(PaymentOutcome.Approved)); }
    }

    private sealed class RecordingPublisher(bool fail = false) : IOrderPublisher
    {
        public List<(OrderRequested Message, string CorrelationId)> Sent { get; } = [];
        public Task PublishAsync(OrderRequested message, string correlationId, CancellationToken ct)
        {
            if (fail) throw new InvalidOperationException("RabbitMQ caido");
            Sent.Add((message, correlationId));
            return Task.CompletedTask;
        }
    }

    private static readonly ProductInfo Yerba = new(1, "Yerba mate 1 kg", 480000, true);
    private static readonly ProductInfo Cafe = new(2, "Cafe molido 250 g", 520000, true);

    private static CreateOrderRequest Items(params (int Id, int Qty)[] lines) => new(lines.Select(l => new OrderLineRequest(l.Id, l.Qty)).ToList());

    private static CheckoutService Build(InMemoryOrders orders, CountingStock stock, CountingPayments pay, IOrderPublisher? publisher, CheckoutMode mode) =>
        new(new StubProducts(Yerba, Cafe), stock, pay, orders, NullLogger<CheckoutService>.Instance, publisher, new CheckoutOptions(mode));

    // ---- modo Async ----
    [Fact]
    public async Task Async_deja_el_pedido_Pendiente_y_publica_OrderRequested_sin_reservar_ni_cobrar()
    {
        var orders = new InMemoryOrders(); var stock = new CountingStock(); var pay = new CountingPayments(); var pub = new RecordingPublisher();
        var r = await Build(orders, stock, pay, pub, CheckoutMode.Async)
            .PlaceAsync("user-1", "key-1", Items((2, 1), (1, 3)), CancellationToken.None, correlationId: "corr-123");

        Assert.True(r.IsValid);
        Assert.True(r.Accepted);
        Assert.False(r.BrokerDown);
        Assert.Equal("Pendiente", r.Order!.Status);
        Assert.Equal("Pendiente", orders.Saved.Single().Status.ToString());
        Assert.Equal(0, stock.Calls);   // reservar y cobrar es trabajo del worker
        Assert.Equal(0, pay.Calls);

        var (message, correlationId) = Assert.Single(pub.Sent);
        Assert.Equal("corr-123", correlationId);
        Assert.Equal(r.Order.Id, message.OrderId);
        Assert.Equal("user-1", message.UserId);
        Assert.Equal(480000L * 3 + 520000L, message.TotalCents);
        Assert.Equal([new OrderItemMessage(1, 3), new OrderItemMessage(2, 1)], message.Items);   // ordenado por producto
    }

    [Fact]
    public async Task Async_sin_correlationId_genera_uno()
    {
        var pub = new RecordingPublisher();
        await Build(new InMemoryOrders(), new CountingStock(), new CountingPayments(), pub, CheckoutMode.Async)
            .PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(pub.Sent).CorrelationId));
    }

    [Fact]
    public async Task Async_con_RabbitMQ_caido_rechaza_el_pedido_con_BrokerNoDisponible()
    {
        var orders = new InMemoryOrders(); var stock = new CountingStock();
        var r = await Build(orders, stock, new CountingPayments(), new RecordingPublisher(fail: true), CheckoutMode.Async)
            .PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.True(r.BrokerDown);
        Assert.False(r.Accepted);
        Assert.False(r.IsValid);
        var saved = Assert.Single(orders.Saved);
        Assert.Equal(OrderStatus.Rechazado, saved.Status);
        Assert.Equal(RejectionReason.BrokerNoDisponible, saved.Reason);
        Assert.Equal(0, stock.Calls);   // todavia no se habia reservado nada: no hay que compensar
    }

    [Fact]
    public async Task Async_reintento_con_la_misma_clave_no_publica_otra_vez()
    {
        var pub = new RecordingPublisher();
        var svc = Build(new InMemoryOrders(), new CountingStock(), new CountingPayments(), pub, CheckoutMode.Async);

        var first = await svc.PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);
        var second = await svc.PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.True(second.Replayed);
        Assert.False(second.Accepted);
        Assert.Equal(first.Order!.Id, second.Order!.Id);
        Assert.Single(pub.Sent);
    }

    [Fact]
    public async Task Async_con_un_pedido_invalido_no_publica_nada()
    {
        var pub = new RecordingPublisher(); var orders = new InMemoryOrders();
        var r = await Build(orders, new CountingStock(), new CountingPayments(), pub, CheckoutMode.Async)
            .PlaceAsync("user-1", "key-1", Items((99, 1)), CancellationToken.None);   // producto inexistente

        Assert.False(r.IsValid);
        Assert.Empty(pub.Sent);
        Assert.Empty(orders.Saved);
    }

    [Fact]
    public async Task Async_sin_publisher_registrado_falla_con_un_mensaje_claro()
    {
        var svc = Build(new InMemoryOrders(), new CountingStock(), new CountingPayments(), publisher: null, CheckoutMode.Async);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None));
    }

    // ---- modo Sync (el actual): no usa RabbitMQ ----
    [Fact]
    public async Task Sync_no_publica_y_deja_el_pedido_en_su_estado_final()
    {
        var pub = new RecordingPublisher(); var stock = new CountingStock(); var pay = new CountingPayments();
        var r = await Build(new InMemoryOrders(), stock, pay, pub, CheckoutMode.Sync).PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.Equal("Confirmado", r.Order!.Status);
        Assert.False(r.Accepted);
        Assert.Empty(pub.Sent);
        Assert.Equal(1, pay.Calls);
    }

    // ---- mensaje y configuracion ----
    [Fact]
    public void OrderRequested_se_serializa_con_los_campos_del_contrato()
    {
        var msg = new OrderRequested(Guid.Parse("3f2a9c1e-5b7d-4e8a-9c21-7d4e5f6a8b90"), "sub-1", [new OrderItemMessage(1, 2)], 960000);

        using var doc = JsonDocument.Parse(msg.ToBytes());
        var root = doc.RootElement;
        Assert.Equal("3f2a9c1e-5b7d-4e8a-9c21-7d4e5f6a8b90", root.GetProperty("orderId").GetString());
        Assert.Equal("sub-1", root.GetProperty("userId").GetString());
        Assert.Equal(960000L, root.GetProperty("totalCents").GetInt64());
        Assert.Equal(1, root.GetProperty("items")[0].GetProperty("productId").GetInt32());
        Assert.Equal(2, root.GetProperty("items")[0].GetProperty("quantity").GetInt32());
    }

    [Fact]
    public void OrderRequested_usa_los_mismos_nombres_que_contracts_order_requested_json()
    {
        var path = RepoFile("contracts", "order-requested.json");
        using var contract = JsonDocument.Parse(File.ReadAllText(path));
        using var actual = JsonDocument.Parse(new OrderRequested(Guid.NewGuid(), "u", [new OrderItemMessage(1, 1)], 1).ToBytes());

        static string[] Keys(JsonElement e) => e.EnumerateObject().Select(p => p.Name).Where(n => !n.StartsWith('$')).Order().ToArray();
        Assert.Equal(Keys(contract.RootElement), Keys(actual.RootElement));
        Assert.Equal(Keys(contract.RootElement.GetProperty("items")[0]), Keys(actual.RootElement.GetProperty("items")[0]));
    }

    [Fact]
    public void OrderRequested_ida_y_vuelta()
    {
        var original = new OrderRequested(Guid.NewGuid(), "sub-1", [new OrderItemMessage(5, 2), new OrderItemMessage(7, 1)], 123456);
        var back = OrderMessages.Parse(original.ToBytes());

        Assert.NotNull(back);
        Assert.Equal(original.OrderId, back!.OrderId);
        Assert.Equal(original.UserId, back.UserId);
        Assert.Equal(original.TotalCents, back.TotalCents);
        Assert.Equal(original.Items, back.Items);
    }

    [Theory]
    [InlineData(null, CheckoutMode.Sync)]
    [InlineData("Sync", CheckoutMode.Sync)]
    [InlineData("Async", CheckoutMode.Async)]
    [InlineData("async", CheckoutMode.Async)]
    [InlineData("cualquier cosa", CheckoutMode.Sync)]
    public void El_modo_sale_de_Checkout_Mode_y_por_defecto_es_Sync(string? configured, CheckoutMode expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(configured is null ? [] : new Dictionary<string, string?> { ["Checkout:Mode"] = configured }).Build();
        var provider = new ServiceCollection().AddLogging().AddOrderMessaging(config).BuildServiceProvider();

        Assert.Equal(expected, provider.GetRequiredService<CheckoutOptions>().Mode);
        Assert.IsType<RabbitOrderPublisher>(provider.GetRequiredService<IOrderPublisher>());   // se crea sin conectarse al broker
    }

    [Fact]
    public void Las_opciones_de_RabbitMQ_tienen_valores_de_desarrollo_y_se_pisan_con_la_configuracion()
    {
        var defaults = RabbitMqOptions.From(new ConfigurationBuilder().Build());
        Assert.Equal(("localhost", "mercadoya", "mercadoya_dev", "/"), (defaults.Host, defaults.User, defaults.Password, defaults.VirtualHost));

        var custom = RabbitMqOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["RabbitMq:Host"] = "rabbitmq", ["RabbitMq:Port"] = "5673", ["RabbitMq:User"] = "u", ["RabbitMq:Password"] = "p" }).Build());
        Assert.Equal(("rabbitmq", 5673, "u", "p"), (custom.Host, custom.Port, custom.User, custom.Password));
    }

    /// Busca un archivo del repositorio subiendo desde la carpeta bin del test.
    private static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"No se encontro {Path.Combine(parts)} subiendo desde {AppContext.BaseDirectory}");
    }
}
