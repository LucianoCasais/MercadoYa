using System.Net;
using MercadoYa.Modules.Pedidos;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace MercadoYa.Tests;

/// Dia 10: Polly en las llamadas del worker a stock y pagos (reintentos, circuit breaker y timeout por intento).
/// Con tiempos cortos para que corran en milisegundos; los valores reales (5 s, 3 reintentos, 30 s) estan en ResilienceOptions.Default.
public class ResilienceTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly IReadOnlyList<OrderLineRequest> Items = [new OrderLineRequest(1, 2)];

    private static ResilienceOptions Fast(int retries = 3, int minThroughput = 100, TimeSpan? attemptTimeout = null, TimeSpan? breakDuration = null) => new(
        AttemptTimeout: attemptTimeout ?? TimeSpan.FromSeconds(2),
        MaxRetries: retries,
        RetryDelay: TimeSpan.FromMilliseconds(1),
        BreakerFailureRatio: 0.5,
        BreakerMinimumThroughput: Math.Max(2, minThroughput),   // por defecto muy alto: el breaker no interviene en los tests de reintentos
        BreakerSamplingDuration: TimeSpan.FromSeconds(30),
        BreakDuration: breakDuration ?? TimeSpan.FromSeconds(30));

    private static HttpRequestException Http(HttpStatusCode? status) => new("falla simulada", null, status);

    // ---- dobles de prueba ----

    /// Stock que falla con la excepcion indicada las primeras N veces y despues responde Reserved.
    private sealed class FlakyStock(Func<Exception>? failure, int failures = int.MaxValue) : IStockClient
    {
        public int ReserveCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public StockReserveOutcome Outcome { get; set; } = StockReserveOutcome.Reserved;
        public TimeSpan Delay { get; set; }

        public async Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct)
        {
            ReserveCalls++;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (failure is not null && ReserveCalls <= failures) throw failure();
            return Outcome;
        }

        public Task ReleaseAsync(Guid orderId, CancellationToken ct)
        {
            ReleaseCalls++;
            if (failure is not null && ReleaseCalls <= failures) throw failure();
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyPayments(Func<PaymentResult> next) : IPaymentGateway
    {
        public int Calls { get; private set; }
        public Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(next());
        }
    }

    private static ResilientStockClient Stock(FlakyStock inner, ResilienceOptions options) =>
        new(inner, ResiliencePipelines.Create("test-stock", options));

    private static ResilientPaymentGateway Payments(IPaymentGateway inner, ResilienceOptions options) =>
        new(inner, ResiliencePipelines.Create("test-payments", options));

    // ---- stock: reintentos ----

    [Fact]
    public async Task Stock_reintenta_un_503_y_termina_bien()
    {
        var inner = new FlakyStock(() => Http(HttpStatusCode.ServiceUnavailable), failures: 2);

        var result = await Stock(inner, Fast()).ReserveAsync(OrderId, Items, CancellationToken.None);

        Assert.Equal(StockReserveOutcome.Reserved, result);
        Assert.Equal(3, inner.ReserveCalls);   // 2 fallos + 1 exito
    }

    [Fact]
    public async Task Stock_reintenta_si_se_cae_la_conexion()
    {
        var inner = new FlakyStock(() => Http(null), failures: 1);   // sin codigo de estado: no llego a haber respuesta

        await Stock(inner, Fast()).ReleaseAsync(OrderId, CancellationToken.None);

        Assert.Equal(2, inner.ReleaseCalls);
    }

    [Fact]
    public async Task Stock_agota_los_reintentos_y_propaga_el_ultimo_error()
    {
        var inner = new FlakyStock(() => Http(HttpStatusCode.BadGateway));

        await Assert.ThrowsAsync<HttpRequestException>(() => Stock(inner, Fast(retries: 3)).ReserveAsync(OrderId, Items, CancellationToken.None));

        Assert.Equal(4, inner.ReserveCalls);   // 1 intento + 3 reintentos
    }

    [Fact]
    public async Task Stock_sin_stock_es_una_respuesta_de_negocio_y_no_se_reintenta()
    {
        var inner = new FlakyStock(failure: null) { Outcome = StockReserveOutcome.InsufficientStock };

        var result = await Stock(inner, Fast()).ReserveAsync(OrderId, Items, CancellationToken.None);

        Assert.Equal(StockReserveOutcome.InsufficientStock, result);
        Assert.Equal(1, inner.ReserveCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Stock_no_reintenta_los_errores_4xx(HttpStatusCode status)
    {
        var inner = new FlakyStock(() => Http(status));

        await Assert.ThrowsAsync<HttpRequestException>(() => Stock(inner, Fast()).ReserveAsync(OrderId, Items, CancellationToken.None));

        Assert.Equal(1, inner.ReserveCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Stock_si_reintenta_408_y_429(HttpStatusCode status)
    {
        var inner = new FlakyStock(() => Http(status), failures: 1);

        await Stock(inner, Fast()).ReserveAsync(OrderId, Items, CancellationToken.None);

        Assert.Equal(2, inner.ReserveCalls);
    }

    [Fact]
    public async Task Stock_no_reintenta_errores_de_logica()
    {
        var inner = new FlakyStock(() => new InvalidOperationException("bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Stock(inner, Fast()).ReserveAsync(OrderId, Items, CancellationToken.None));

        Assert.Equal(1, inner.ReserveCalls);
    }

    // ---- stock: timeout y cancelacion ----

    [Fact]
    public async Task Stock_corta_un_intento_colgado_con_el_timeout_y_lo_reintenta()
    {
        var inner = new FlakyStock(failure: null) { Delay = TimeSpan.FromSeconds(5) };
        var options = Fast(retries: 2, attemptTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => Stock(inner, options).ReserveAsync(OrderId, Items, CancellationToken.None));

        Assert.Equal(3, inner.ReserveCalls);
    }

    [Fact]
    public async Task Stock_si_el_llamador_cancela_no_se_reintenta()
    {
        var inner = new FlakyStock(() => Http(HttpStatusCode.ServiceUnavailable));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Stock(inner, Fast()).ReserveAsync(OrderId, Items, cts.Token));

        Assert.Equal(0, inner.ReserveCalls);
    }

    // ---- circuit breaker ----

    [Fact]
    public async Task El_circuito_se_abre_tras_varios_fallos_y_las_llamadas_siguientes_fallan_sin_intentarse()
    {
        var inner = new FlakyStock(() => Http(HttpStatusCode.ServiceUnavailable));
        var stock = Stock(inner, Fast(retries: 0, minThroughput: 3));

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() => stock.ReserveAsync(OrderId, Items, CancellationToken.None));
        Assert.Equal(3, inner.ReserveCalls);

        await Assert.ThrowsAsync<BrokenCircuitException>(() => stock.ReserveAsync(OrderId, Items, CancellationToken.None));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => stock.ReleaseAsync(OrderId, CancellationToken.None));
        Assert.Equal(3, inner.ReserveCalls);   // no se llamo al servicio caido
        Assert.Equal(0, inner.ReleaseCalls);
    }

    [Fact]
    public async Task Cuando_el_circuito_se_abre_en_medio_de_los_reintentos_se_corta_enseguida()
    {
        var inner = new FlakyStock(() => Http(HttpStatusCode.ServiceUnavailable));
        var stock = Stock(inner, Fast(retries: 3, minThroughput: 2));

        // Intento 1 y 2 fallan y abren el circuito; el 3 ya no se hace (aunque quedaban reintentos): un circuito abierto no se reintenta.
        await Assert.ThrowsAsync<BrokenCircuitException>(() => stock.ReserveAsync(OrderId, Items, CancellationToken.None));

        Assert.Equal(2, inner.ReserveCalls);
    }

    [Fact]
    public async Task El_circuito_se_cierra_cuando_el_servicio_se_recupera()
    {
        var healthy = false;
        var inner = new FlakyStock(() => Http(HttpStatusCode.ServiceUnavailable));
        var gate = new ToggleStock(inner, () => healthy);
        var stock = new ResilientStockClient(gate, ResiliencePipelines.Create("test", Fast(retries: 0, minThroughput: 2, breakDuration: TimeSpan.FromMilliseconds(600))));

        await Assert.ThrowsAsync<HttpRequestException>(() => stock.ReserveAsync(OrderId, Items, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => stock.ReserveAsync(OrderId, Items, CancellationToken.None));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => stock.ReserveAsync(OrderId, Items, CancellationToken.None));

        healthy = true;
        await Task.Delay(900);   // pasa el BreakDuration (Polly exige al menos 500 ms): la proxima llamada es la prueba (semiabierto)

        Assert.Equal(StockReserveOutcome.Reserved, await stock.ReserveAsync(OrderId, Items, CancellationToken.None));
        Assert.Equal(StockReserveOutcome.Reserved, await stock.ReserveAsync(OrderId, Items, CancellationToken.None));   // ya cerrado
    }

    private sealed class ToggleStock(FlakyStock failing, Func<bool> healthy) : IStockClient
    {
        public Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct) =>
            healthy() ? Task.FromResult(StockReserveOutcome.Reserved) : failing.ReserveAsync(orderId, items, ct);
        public Task ReleaseAsync(Guid orderId, CancellationToken ct) => failing.ReleaseAsync(orderId, ct);
    }

    // ---- pagos ----

    [Fact]
    public async Task Pago_aprobado_pasa_sin_reintentos()
    {
        var inner = new FlakyPayments(() => new PaymentResult(PaymentOutcome.Approved));

        var result = await Payments(inner, Fast()).ChargeAsync(OrderId, 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Approved, result.Outcome);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Pago_rechazado_es_una_respuesta_valida_y_no_se_reintenta()
    {
        var inner = new FlakyPayments(() => new PaymentResult(PaymentOutcome.Rejected, "Pago simulado rechazado"));

        var result = await Payments(inner, Fast()).ChargeAsync(OrderId, 1999, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Rejected, result.Outcome);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Pago_no_disponible_se_reintenta_y_si_se_recupera_se_aprueba()
    {
        var calls = 0;
        var inner = new FlakyPayments(() => ++calls < 3 ? new PaymentResult(PaymentOutcome.Unavailable) : new PaymentResult(PaymentOutcome.Approved));

        var result = await Payments(inner, Fast()).ChargeAsync(OrderId, 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Approved, result.Outcome);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Pago_caido_agota_los_reintentos_y_devuelve_no_disponible()
    {
        var inner = new FlakyPayments(() => new PaymentResult(PaymentOutcome.Unavailable));

        var result = await Payments(inner, Fast(retries: 3)).ChargeAsync(OrderId, 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Unavailable, result.Outcome);
        Assert.Equal(4, inner.Calls);
    }

    [Fact]
    public async Task Pago_que_lanza_una_excepcion_transitoria_se_reintenta_y_termina_no_disponible()
    {
        var gateway = new ThrowingPayments(() => new HttpRequestException("caido"));

        var result = await Payments(gateway, Fast(retries: 2)).ChargeAsync(OrderId, 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Unavailable, result.Outcome);
        Assert.Equal(3, gateway.Calls);
    }

    [Fact]
    public async Task Con_el_circuito_de_pagos_abierto_responde_no_disponible_sin_llamar_al_servicio()
    {
        var inner = new FlakyPayments(() => new PaymentResult(PaymentOutcome.Unavailable));
        var payments = Payments(inner, Fast(retries: 0, minThroughput: 3));
        for (var i = 0; i < 3; i++) await payments.ChargeAsync(OrderId, 1000, CancellationToken.None);
        Assert.Equal(3, inner.Calls);

        var result = await payments.ChargeAsync(OrderId, 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Unavailable, result.Outcome);
        Assert.Equal(3, inner.Calls);   // fallo rapido: no se espero a un servicio que ya se sabe caido
    }

    [Fact]
    public async Task Pago_si_se_apaga_el_worker_se_propaga_la_cancelacion()
    {
        var inner = new FlakyPayments(() => new PaymentResult(PaymentOutcome.Approved));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Payments(inner, Fast()).ChargeAsync(OrderId, 1000, cts.Token));

        Assert.Equal(0, inner.Calls);
    }

    private sealed class ThrowingPayments(Func<Exception> failure) : IPaymentGateway
    {
        public int Calls { get; private set; }
        public Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct)
        {
            Calls++;
            throw failure();
        }
    }
}
