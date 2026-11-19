using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MercadoYa.Tests;

public class CheckoutServiceTests
{
    // ---- dobles de prueba (sin base de datos) ----
    private sealed class InMemoryOrders : IOrderRepository
    {
        public List<Order> Saved { get; } = [];
        public Task<Order?> FindByKeyAsync(string userId, string key, CancellationToken ct) =>
            Task.FromResult(Saved.FirstOrDefault(o => o.UserId == userId && o.IdempotencyKey == key));
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

    private sealed class ScriptedStock(StockReserveOutcome outcome = StockReserveOutcome.Reserved, bool throwOnReserve = false) : IStockClient
    {
        public int Reserves { get; private set; }
        public int Releases { get; private set; }
        public Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct)
        {
            Reserves++;
            if (throwOnReserve) throw new InvalidOperationException("stock caido");
            return Task.FromResult(outcome);
        }
        public Task ReleaseAsync(Guid orderId, CancellationToken ct) { Releases++; return Task.CompletedTask; }
    }

    private sealed class ScriptedPayments(PaymentOutcome outcome = PaymentOutcome.Approved, bool throwOnCharge = false) : IPaymentGateway
    {
        public int Charges { get; private set; }
        public Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct)
        {
            Charges++;
            if (throwOnCharge) throw new InvalidOperationException("payments caido");
            return Task.FromResult(new PaymentResult(outcome));
        }
    }

    private static readonly ProductInfo Yerba = new(1, "Yerba mate 1 kg", 480000, true);
    private static readonly ProductInfo Cafe = new(2, "Cafe molido 250 g", 520000, true);
    private static readonly ProductInfo Baja = new(3, "Producto dado de baja", 100000, false);

    private static CheckoutService Build(InMemoryOrders orders, ScriptedStock stock, ScriptedPayments payments) =>
        new(new StubProducts(Yerba, Cafe, Baja), stock, payments, orders, NullLogger<CheckoutService>.Instance);

    private static CreateOrderRequest Items(params (int Id, int Qty)[] lines) =>
        new(lines.Select(l => new OrderLineRequest(l.Id, l.Qty)).ToList());

    [Fact]
    public async Task Compra_aprobada_queda_Confirmada_con_precios_congelados()
    {
        var stock = new ScriptedStock(); var pay = new ScriptedPayments();
        var r = await Build(new InMemoryOrders(), stock, pay).PlaceAsync("user-1", "key-1", Items((1, 2), (2, 1)), CancellationToken.None);

        Assert.True(r.IsValid);
        Assert.Equal("Confirmado", r.Order!.Status);
        Assert.Null(r.Order.Reason);
        Assert.Equal(480000L * 2 + 520000L, r.Order.TotalCents);
        Assert.Equal(2, r.Order.Items.Count);
        Assert.Equal(1, stock.Reserves);
        Assert.Equal(1, pay.Charges);
        Assert.Equal(0, stock.Releases);
    }

    [Fact]
    public async Task Sin_stock_rechaza_sin_cobrar_ni_liberar()
    {
        var stock = new ScriptedStock(StockReserveOutcome.InsufficientStock); var pay = new ScriptedPayments();
        var r = await Build(new InMemoryOrders(), stock, pay).PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.Equal("Rechazado", r.Order!.Status);
        Assert.Equal("SinStock", r.Order.Reason);
        Assert.Equal(0, pay.Charges);
        Assert.Equal(0, stock.Releases);
    }

    [Fact]
    public async Task Stock_caido_rechaza_con_StockNoDisponible_e_intenta_liberar()
    {
        var stock = new ScriptedStock(throwOnReserve: true); var pay = new ScriptedPayments();
        var r = await Build(new InMemoryOrders(), stock, pay).PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.Equal("Rechazado", r.Order!.Status);
        Assert.Equal("StockNoDisponible", r.Order.Reason);
        Assert.Equal(1, stock.Releases);
        Assert.Equal(0, pay.Charges);
    }

    [Fact]
    public async Task Pago_rechazado_libera_el_stock()
    {
        var stock = new ScriptedStock(); var pay = new ScriptedPayments(PaymentOutcome.Rejected);
        var r = await Build(new InMemoryOrders(), stock, pay).PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.Equal("Rechazado", r.Order!.Status);
        Assert.Equal("PagoFallido", r.Order.Reason);
        Assert.Equal(1, stock.Releases);
    }

    [Fact]
    public async Task Pagos_caido_libera_el_stock_y_rechaza_con_PagoNoDisponible()
    {
        var stock = new ScriptedStock(); var pay = new ScriptedPayments(throwOnCharge: true);
        var r = await Build(new InMemoryOrders(), stock, pay).PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.Equal("Rechazado", r.Order!.Status);
        Assert.Equal("PagoNoDisponible", r.Order.Reason);
        Assert.Equal(1, stock.Releases);
    }

    [Fact]
    public async Task Reintento_con_la_misma_clave_devuelve_el_mismo_pedido_sin_reservar_otra_vez()
    {
        var stock = new ScriptedStock(); var pay = new ScriptedPayments();
        var svc = Build(new InMemoryOrders(), stock, pay);

        var first = await svc.PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);
        var second = await svc.PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Order!.Id, second.Order!.Id);
        Assert.Equal(1, stock.Reserves);
        Assert.Equal(1, pay.Charges);
    }

    [Fact]
    public async Task La_misma_clave_en_otro_usuario_crea_otro_pedido()
    {
        var svc = Build(new InMemoryOrders(), new ScriptedStock(), new ScriptedPayments());

        var a = await svc.PlaceAsync("user-1", "key-1", Items((1, 1)), CancellationToken.None);
        var b = await svc.PlaceAsync("user-2", "key-1", Items((1, 1)), CancellationToken.None);

        Assert.False(b.Replayed);
        Assert.False(a.Order!.Id == b.Order!.Id);
    }

    [Fact]
    public async Task Producto_inexistente_o_dado_de_baja_es_invalido()
    {
        var svc = Build(new InMemoryOrders(), new ScriptedStock(), new ScriptedPayments());

        Assert.False((await svc.PlaceAsync("user-1", "k1", Items((999, 1)), CancellationToken.None)).IsValid);
        Assert.False((await svc.PlaceAsync("user-1", "k2", Items((3, 1)), CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task Sin_Idempotency_Key_es_invalido()
    {
        var svc = Build(new InMemoryOrders(), new ScriptedStock(), new ScriptedPayments());

        Assert.False((await svc.PlaceAsync("user-1", null, Items((1, 1)), CancellationToken.None)).IsValid);
        Assert.False((await svc.PlaceAsync("user-1", "   ", Items((1, 1)), CancellationToken.None)).IsValid);
        Assert.False((await svc.PlaceAsync("user-1", new string('x', 101), Items((1, 1)), CancellationToken.None)).IsValid);
    }

    [Fact]
    public void Normalize_suma_los_productos_repetidos()
    {
        var lines = CheckoutService.Normalize(Items((2, 1), (1, 2), (2, 3)).Items, out var error);

        Assert.Null(error);
        Assert.Equal(2, lines!.Count);
        Assert.Equal(1, lines[0].ProductId);
        Assert.Equal(2, lines[0].Quantity);
        Assert.Equal(2, lines[1].ProductId);
        Assert.Equal(4, lines[1].Quantity);
    }

    [Fact]
    public void Normalize_rechaza_pedido_vacio_cantidades_invalidas_y_excesos()
    {
        Assert.Null(CheckoutService.Normalize(null, out _));
        Assert.Null(CheckoutService.Normalize(Items().Items, out _));
        Assert.Null(CheckoutService.Normalize(Items((1, 0)).Items, out _));
        Assert.Null(CheckoutService.Normalize(Items((1, -2)).Items, out _));
        Assert.Null(CheckoutService.Normalize(Items((0, 1)).Items, out _));
        Assert.Null(CheckoutService.Normalize(Items((1, 60), (1, 60)).Items, out _));   // 120 > 99 una vez unificado
    }

    [Fact]
    public async Task El_pago_fake_rechaza_importes_que_terminan_en_999_centavos()
    {
        var gw = new FakePaymentGateway();

        Assert.Equal(PaymentOutcome.Rejected, (await gw.ChargeAsync(Guid.NewGuid(), 999, CancellationToken.None)).Outcome);
        Assert.Equal(PaymentOutcome.Approved, (await gw.ChargeAsync(Guid.NewGuid(), 480000, CancellationToken.None)).Outcome);
    }
}
