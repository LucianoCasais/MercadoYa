using System.Net;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PaymentsGrpc = MercadoYa.Payments.Grpc.Payments;

namespace MercadoYa.Tests;

public class GrpcPaymentGatewayCodesTests
{
    [Theory]
    [InlineData(StatusCode.Unavailable, true)]
    [InlineData(StatusCode.DeadlineExceeded, true)]
    [InlineData(StatusCode.ResourceExhausted, true)]
    [InlineData(StatusCode.Internal, true)]
    [InlineData(StatusCode.InvalidArgument, false)]
    [InlineData(StatusCode.FailedPrecondition, false)]
    [InlineData(StatusCode.Unauthenticated, false)]
    [InlineData(StatusCode.PermissionDenied, false)]
    [InlineData(StatusCode.Unimplemented, false)]
    public void Solo_se_reintentan_los_errores_que_pueden_andar_en_el_proximo_intento(StatusCode code, bool transient) =>
        Assert.Equal(transient, GrpcPaymentGateway.IsTransient(code));
}

/// GrpcPaymentGateway contra el servicio Payments REAL en memoria (PaymentsHost: auth, gRPC, BD payments en PostgreSQL).
/// Keycloak es un handler falso que entrega el token firmado por el host del test (como el client credentials real).
[Collection("payments-db")]
[Trait("Category", "Integracion")]
public sealed class GrpcPaymentGatewayTests(PaymentsDbFixture pg) : IDisposable
{
    private const string TokenUrl = "http://keycloak.test/realms/mercadoya/protocol/openid-connect/token";
    private readonly PaymentsHost _host = new(pg.ConnectionString);

    public void Dispose() => _host.Dispose();

    // ---- dobles de prueba ----

    /// Cuenta los intentos que salen hacia Payments y los pasa al handler real (o al que se indique).
    private sealed class CountingHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(inner, disposeHandler: false);
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return _inner.SendAsync(request, ct);
        }
    }

    /// Payments que nunca responde (servicio colgado): solo termina cuando se cancela la llamada.
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("inalcanzable");
        }
    }

    /// Keycloak falso para ServiceTokenProvider: devuelve siempre el token indicado.
    private sealed class KeycloakHandler(string token) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"{{token}}","expires_in":300}""", Encoding.UTF8, "application/json"),
            });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private ServiceTokenProvider Tokens(string azp = "orders-worker") =>
        new(new Factory(new KeycloakHandler(_host.Token(azp))), new ServiceTokenOptions(TokenUrl, "orders-worker", "secreto"), TimeProvider.System);

    /// Gateway con el canal configurado igual que AddGrpcPaymentGateway (OperationCanceledException al cancelarse).
    private GrpcPaymentGateway Gateway(HttpMessageHandler handler, ServiceTokenProvider? tokens = null, Uri? address = null, bool throwOnCancel = true) =>
        new(new PaymentsGrpc.PaymentsClient(GrpcChannel.ForAddress(address ?? _host.Server.BaseAddress,
                new GrpcChannelOptions { HttpHandler = handler, ThrowOperationCanceledOnCancellation = throwOnCancel })),
            tokens ?? Tokens(), NullLogger<GrpcPaymentGateway>.Instance);

    /// Polly como en el worker pero rapido: 3 reintentos casi sin espera y un breaker que no llega a abrirse en el test.
    private static ResilientPaymentGateway Resilient(IPaymentGateway inner, TimeSpan? attemptTimeout = null) =>
        new(inner, ResiliencePipelines.Create("payments-test", new ResilienceOptions(attemptTimeout ?? TimeSpan.FromSeconds(5), 3,
            TimeSpan.FromMilliseconds(1), 0.5, 1000, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30))));

    // ---- resultados de negocio ----

    [Fact]
    public async Task Cobro_aprobado_por_Payments_llega_como_Approved()
    {
        var order = Guid.NewGuid();

        var r = await Gateway(_host.Server.CreateHandler()).ChargeAsync(order, 480000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Approved, r.Outcome);
        Assert.Single(await pg.RecordsOfAsync(order));
    }

    [Fact]
    public async Task Cobro_rechazado_llega_como_Rejected_con_el_motivo_de_Payments()
    {
        var r = await Gateway(_host.Server.CreateHandler()).ChargeAsync(Guid.NewGuid(), 999, CancellationToken.None);

        Assert.Equal((PaymentOutcome.Rejected, "Pago simulado rechazado"), (r.Outcome, r.Reason));
    }

    [Fact]
    public async Task Repetir_el_cobro_del_mismo_pedido_no_cobra_dos_veces()
    {
        var gw = Gateway(_host.Server.CreateHandler());
        var order = Guid.NewGuid();

        var first = await gw.ChargeAsync(order, 250000, CancellationToken.None);
        var again = await gw.ChargeAsync(order, 250000, CancellationToken.None);

        Assert.Equal(first, again);
        Assert.Single(await pg.RecordsOfAsync(order));
    }

    // ---- fallas transitorias: Unavailable y Polly reintenta ----

    [Fact]
    public async Task Base_de_pagos_caida_devuelve_Unavailable_y_Polly_reintenta()
    {
        using var caido = new PaymentsHost("Host=127.0.0.1;Port=1;Database=payments;Username=x;Password=x;Timeout=2");
        var counter = new CountingHandler(caido.Server.CreateHandler());
        var tokens = new ServiceTokenProvider(new Factory(new KeycloakHandler(caido.Token("orders-worker"))),
            new ServiceTokenOptions(TokenUrl, "orders-worker", "secreto"), TimeProvider.System);

        var r = await Resilient(Gateway(counter, tokens, caido.Server.BaseAddress)).ChargeAsync(Guid.NewGuid(), 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Unavailable, r.Outcome);
        Assert.Equal(4, counter.Calls);   // 1 intento + 3 reintentos
    }

    [Fact]
    public async Task Payments_inaccesible_devuelve_Unavailable()
    {
        var gw = Gateway(new SocketsHttpHandler(), address: new Uri("http://127.0.0.1:1"));

        var r = await gw.ChargeAsync(Guid.NewGuid(), 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    public async Task Payments_colgado_cuenta_como_timeout_de_Polly_y_se_reintenta()
    {
        var counter = new CountingHandler(new HangingHandler());

        var r = await Resilient(Gateway(counter), attemptTimeout: TimeSpan.FromMilliseconds(100)).ChargeAsync(Guid.NewGuid(), 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Unavailable, r.Outcome);
        Assert.Equal(4, counter.Calls);
    }

    [Fact]
    public async Task Control_sin_ThrowOperationCanceledOnCancellation_el_timeout_no_se_reintentaria()
    {
        // Muestra POR QUE el canal se configura asi: sin la opcion, el corte de Polly llega como RpcException(Cancelled),
        // que no parece un timeout, y el cobro colgado se abandona al primer intento (sin reintentos ni breaker).
        var counter = new CountingHandler(new HangingHandler());

        var r = await Resilient(Gateway(counter, throwOnCancel: false), attemptTimeout: TimeSpan.FromMilliseconds(100)).ChargeAsync(Guid.NewGuid(), 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Unavailable, r.Outcome);
        Assert.Equal(1, counter.Calls);
    }

    // ---- errores que no se arreglan reintentando ----

    [Fact]
    public async Task Token_de_otro_client_lanza_PaymentGatewayException_PermissionDenied()
    {
        var gw = Gateway(_host.Server.CreateHandler(), Tokens(azp: "web"));

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => gw.ChargeAsync(Guid.NewGuid(), 1000, CancellationToken.None));

        Assert.Equal(StatusCode.PermissionDenied, ex.Code);
    }

    [Fact]
    public async Task Error_no_transitorio_no_se_reintenta_y_el_pedido_queda_no_disponible()
    {
        var counter = new CountingHandler(_host.Server.CreateHandler());

        var r = await Resilient(Gateway(counter, Tokens(azp: "web"))).ChargeAsync(Guid.NewGuid(), 1000, CancellationToken.None);

        Assert.Equal(PaymentOutcome.Unavailable, r.Outcome);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task Mismo_pedido_con_otro_importe_lanza_FailedPrecondition()
    {
        var gw = Gateway(_host.Server.CreateHandler());
        var order = Guid.NewGuid();
        await gw.ChargeAsync(order, 480000, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => gw.ChargeAsync(order, 1000, CancellationToken.None));

        Assert.Equal(StatusCode.FailedPrecondition, ex.Code);
    }

    // ---- registro real del worker ----

    /// Keycloak (por host) y Payments en memoria detras de un solo handler: lo usan TODOS los HttpClient del contenedor de DI.
    private sealed class RoutingHandler(string token, HttpMessageHandler payments) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _keycloak = new(new KeycloakHandler(token));
        private readonly HttpMessageInvoker _payments = new(payments, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.Host == "keycloak.test" ? _keycloak.SendAsync(request, ct) : _payments.SendAsync(request, ct);
    }

    private ServiceProvider WorkerServices(HttpMessageHandler payments)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:GrpcUrl"] = _host.Server.BaseAddress.ToString(),
            ["Keycloak:TokenUrl"] = TokenUrl,
            ["Keycloak:ClientId"] = "orders-worker",
            ["Keycloak:ClientSecret"] = "secreto",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new RoutingHandler(_host.Token("orders-worker"), payments)));
        services.AddGrpcPaymentGateway(config);   // exactamente lo que hace el Program.cs del worker
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AddGrpcPaymentGateway_arma_el_gateway_real_con_token_de_servicio_y_Polly()
    {
        await using var sp = WorkerServices(_host.Server.CreateHandler());
        await using var scope = sp.CreateAsyncScope();
        var order = Guid.NewGuid();

        var gw = scope.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var r = await gw.ChargeAsync(order, 480000, CancellationToken.None);

        Assert.IsType<ResilientPaymentGateway>(gw);
        Assert.Equal(PaymentOutcome.Approved, r.Outcome);
        Assert.Single(await pg.RecordsOfAsync(order));
    }

    [Fact]
    public async Task AddGrpcPaymentGateway_configura_el_canal_para_que_Polly_reconozca_el_timeout()
    {
        await using var sp = WorkerServices(new HangingHandler());
        await using var scope = sp.CreateAsyncScope();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var gw = scope.ServiceProvider.GetRequiredService<GrpcPaymentGateway>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gw.ChargeAsync(Guid.NewGuid(), 1000, cts.Token));
    }
}
