extern alias payments;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Payments.Grpc;
using MercadoYa.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PaymentsGrpc = MercadoYa.Payments.Grpc.Payments;

namespace MercadoYa.Tests;

/// Reglas del id de correlacion y del handler que lo propaga (sin red ni base).
public class CorrelationUnitTests
{
    [Theory]
    [InlineData("demo-123")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("pedido_42.reintento")]
    public void Un_id_razonable_se_respeta(string id) => Assert.Equal(id, CorrelationContext.FromIncoming(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tiene espacios")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("linea\nnueva")]
    public void Un_id_vacio_o_peligroso_se_reemplaza_por_uno_nuevo(string? id)
    {
        var result = CorrelationContext.FromIncoming(id);

        Assert.NotEqual(id, result);
        Assert.True(CorrelationContext.IsValid(result));
    }

    [Fact]
    public void Un_id_demasiado_largo_se_reemplaza() =>
        Assert.NotEqual(new string('a', 65), CorrelationContext.FromIncoming(new string('a', 65)));

    [Fact]
    public void Begin_fija_el_id_y_al_terminar_vuelve_el_anterior()
    {
        Assert.Null(CorrelationContext.Current);
        using (CorrelationContext.Begin("afuera"))
        {
            using (CorrelationContext.Begin("adentro"))
                Assert.Equal("adentro", CorrelationContext.Current);
            Assert.Equal("afuera", CorrelationContext.Current);
        }
        Assert.Null(CorrelationContext.Current);
    }

    [Fact]
    public async Task El_id_sigue_disponible_despues_de_un_await()
    {
        using (CorrelationContext.Begin("async-1"))
        {
            await Task.Yield();
            Assert.Equal("async-1", CorrelationContext.Current);
        }
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task<HttpRequestMessage> SendThroughHandler(HttpRequestMessage request)
    {
        var recorder = new Recorder();
        using var http = new HttpClient(new CorrelationIdHandler { InnerHandler = recorder });
        await http.SendAsync(request);
        return recorder.Last!;
    }

    [Fact]
    public async Task El_handler_agrega_el_id_de_la_operacion_en_curso()
    {
        using (CorrelationContext.Begin("op-77"))
        {
            var sent = await SendThroughHandler(new HttpRequestMessage(HttpMethod.Get, "http://x/"));
            Assert.Equal("op-77", sent.Headers.GetValues(Headers.CorrelationId).Single());
        }
    }

    [Fact]
    public async Task Sin_operacion_en_curso_no_agrega_nada()
    {
        var sent = await SendThroughHandler(new HttpRequestMessage(HttpMethod.Get, "http://x/"));
        Assert.False(sent.Headers.Contains(Headers.CorrelationId));
    }

    [Fact]
    public async Task Si_la_request_ya_trae_un_id_no_lo_pisa()
    {
        using (CorrelationContext.Begin("op-77"))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://x/");
            request.Headers.Add(Headers.CorrelationId, "explicito");
            var sent = await SendThroughHandler(request);
            Assert.Equal("explicito", sent.Headers.GetValues(Headers.CorrelationId).Single());
        }
    }

    // ---- registro real del worker: el cliente HTTP de stock propaga el id ----

    /// Keycloak falso (por host) + "API" que anota la request de stock.
    private sealed class FakeNetwork : HttpMessageHandler
    {
        public HttpRequestMessage? StockRequest;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "keycloak.test")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"tok","expires_in":300}""", Encoding.UTF8, "application/json"),
                });
            StockRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task El_cliente_de_stock_del_worker_manda_el_X_Correlation_Id()
    {
        var network = new FakeNetwork();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Api:BaseUrl"] = "http://api.test",
            ["Keycloak:TokenUrl"] = "http://keycloak.test/token",
            ["Keycloak:ClientId"] = "orders-worker",
            ["Keycloak:ClientSecret"] = "x",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => network));
        services.AddStockHttpClient(config, resilient: true);   // lo mismo que el Program.cs del worker
        await using var sp = services.BuildServiceProvider();
        var stock = sp.GetRequiredService<IStockClient>();

        using (CorrelationContext.Begin("pedido-abc"))
            await stock.ReleaseAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("pedido-abc", network.StockRequest!.Headers.GetValues(Headers.CorrelationId).Single());
    }
}

/// La API completa: toma, genera y devuelve el X-Correlation-Id y lo escribe en sus logs.
[Collection("postgres")]
[Trait("Category", "Integracion")]
public sealed class CorrelationApiTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiHost _api = new(pg.ConnectionString);

    public void Dispose() => _api.Dispose();

    private static string IdOf(HttpResponseMessage r) => r.Headers.GetValues(Headers.CorrelationId).Single();

    [Fact]
    public async Task Devuelve_el_id_que_recibe()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/categories");
        request.Headers.Add(Headers.CorrelationId, "web-123");

        Assert.Equal("web-123", IdOf(await _api.Client().SendAsync(request)));
    }

    [Fact]
    public async Task Sin_id_genera_uno_y_lo_devuelve()
    {
        var id = IdOf(await _api.Client().GetAsync("/api/v1/categories"));

        Assert.True(CorrelationContext.IsValid(id));
    }

    [Fact]
    public async Task Un_id_invalido_se_reemplaza()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/categories");
        request.Headers.TryAddWithoutValidation(Headers.CorrelationId, "<b>inyeccion</b>");

        var id = IdOf(await _api.Client().SendAsync(request));

        Assert.NotEqual("<b>inyeccion</b>", id);
        Assert.True(CorrelationContext.IsValid(id));
    }

    [Fact]
    public async Task Tambien_se_devuelve_en_las_respuestas_de_error()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/products");   // 401 sin token
        request.Headers.Add(Headers.CorrelationId, "err-401");

        var r = await _api.Client().SendAsync(request);

        Assert.Equal((HttpStatusCode.Unauthorized, "err-401"), (r.StatusCode, IdOf(r)));
    }

    [Fact]
    public async Task Los_logs_de_la_request_llevan_el_CorrelationId_y_el_servicio()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/categories");
        request.Headers.Add(Headers.CorrelationId, "log-api-1");

        await _api.Client().SendAsync(request);

        var e = Assert.Single(_api.Logs.WithCorrelation("log-api-1"), x => CapturingSink.Text(x).Contains("/api/v1/categories"));
        Assert.Equal("\"api\"", e.Properties["Service"].ToString());
    }
}

/// Payments: el id llega como metadata gRPC (header HTTP/2), se devuelve y queda en sus logs.
[Collection("payments-db")]
[Trait("Category", "Integracion")]
public sealed class CorrelationPaymentsTests(PaymentsDbFixture pg) : IDisposable
{
    private readonly PaymentsHost _payments = new(pg.ConnectionString);

    public void Dispose() => _payments.Dispose();

    [Fact]
    public async Task La_metadata_gRPC_llega_a_Payments_se_devuelve_y_queda_en_sus_logs()
    {
        var channel = GrpcChannel.ForAddress(_payments.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = new CorrelationIdHandler { InnerHandler = _payments.Server.CreateHandler() } });
        var client = new PaymentsGrpc.PaymentsClient(channel);
        var auth = new Metadata { { "Authorization", $"Bearer {_payments.Token("orders-worker")}" } };

        using (CorrelationContext.Begin("cobro-xyz"))
        {
            var call = client.ChargeOrderAsync(new ChargeOrderRequest { OrderId = Guid.NewGuid().ToString(), AmountCents = 1000 }, auth);
            var reply = await call.ResponseAsync;
            var headers = await call.ResponseHeadersAsync;

            Assert.Equal(PaymentStatus.Aprobado, reply.Status);
            Assert.Equal("cobro-xyz", headers.GetValue("x-correlation-id"));
        }
        Assert.Contains(_payments.Logs.WithCorrelation("cobro-xyz"), e => e.MessageTemplate.Text.StartsWith("Cobro {Status}"));
    }

    [Fact]
    public async Task El_gateway_del_worker_propaga_el_id_hasta_los_logs_de_Payments()
    {
        // Mismo registro que el Program.cs del worker (AddGrpcPaymentGateway), con Keycloak falso y Payments en memoria.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:GrpcUrl"] = _payments.Server.BaseAddress.ToString(),
            ["Keycloak:TokenUrl"] = "http://keycloak.test/token",
            ["Keycloak:ClientId"] = "orders-worker",
            ["Keycloak:ClientSecret"] = "x",
        }).Build();
        var token = _payments.Token("orders-worker");
        var services = new ServiceCollection().AddLogging();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new KeycloakOr(token, _payments.Server.CreateHandler())));
        services.AddGrpcPaymentGateway(config);
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IPaymentGateway>();

        using (CorrelationContext.Begin("saga-777"))
            Assert.Equal(PaymentOutcome.Approved, (await gateway.ChargeAsync(Guid.NewGuid(), 2000, CancellationToken.None)).Outcome);

        Assert.Contains(_payments.Logs.WithCorrelation("saga-777"), e => e.MessageTemplate.Text.StartsWith("Cobro {Status}"));
    }

    private sealed class KeycloakOr(string token, HttpMessageHandler payments) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _payments = new(payments, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.Host == "keycloak.test"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"access_token":"{{token}}","expires_in":300}""", Encoding.UTF8, "application/json"),
                })
                : _payments.SendAsync(request, ct);
    }
}
