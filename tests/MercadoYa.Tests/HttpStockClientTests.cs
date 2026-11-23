using System.Net;
using System.Text;
using System.Text.Json;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using Xunit;

namespace MercadoYa.Tests;

public class HttpStockClientTests
{
    // ---- dobles de prueba ----
    private sealed record Seen(HttpMethod Method, string Url, string? Auth, string Body);

    /// Handler que anota cada request y responde lo que diga `respond`.
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Seen(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly ServiceTokenOptions Opts = new("http://kc/token", "orders-worker", "secreto");

    /// Keycloak falso: entrega "tok-1", "tok-2"... con la vigencia indicada.
    private static (RecordingHandler Handler, ServiceTokenProvider Provider, ManualClock Clock) Tokens(int expiresIn = 300, HttpStatusCode code = HttpStatusCode.OK)
    {
        var n = 0;
        var handler = new RecordingHandler(_ => code == HttpStatusCode.OK
            ? Json($$"""{"access_token":"tok-{{++n}}","expires_in":{{expiresIn}}}""")
            : Json("{}", code));
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        return (handler, new ServiceTokenProvider(new Factory(handler), Opts, clock), clock);
    }

    private static (HttpStockClient Client, RecordingHandler Api, RecordingHandler Keycloak) Stock(Func<HttpRequestMessage, HttpResponseMessage> api)
    {
        var (kc, provider, _) = Tokens();
        var handler = new RecordingHandler(api);
        var client = new HttpStockClient(new HttpClient(handler) { BaseAddress = new Uri("http://api") }, provider);
        return (client, handler, kc);
    }

    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly IReadOnlyList<OrderLineRequest> Lines = [new(1, 2), new(5, 1)];

    // ---- HttpStockClient ----
    [Fact]
    public async Task Reservar_hace_POST_con_token_y_cuerpo_camelCase()
    {
        var (client, api, _) = Stock(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var outcome = await client.ReserveAsync(OrderId, Lines, default);

        Assert.Equal(StockReserveOutcome.Reserved, outcome);
        var req = Assert.Single(api.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://api/internal/v1/stock/reservations", req.Url);
        Assert.Equal("Bearer tok-1", req.Auth);
        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal(OrderId.ToString(), doc.RootElement.GetProperty("orderId").GetString());
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(1, items[0].GetProperty("productId").GetInt32());
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Reservar_con_409_es_sin_stock()
    {
        var (client, _, _) = Stock(_ => new HttpResponseMessage(HttpStatusCode.Conflict));
        Assert.Equal(StockReserveOutcome.InsufficientStock, await client.ReserveAsync(OrderId, Lines, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Reservar_con_otro_error_lanza_excepcion(HttpStatusCode code)
    {
        var (client, _, _) = Stock(_ => new HttpResponseMessage(code));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReserveAsync(OrderId, Lines, default));
    }

    [Fact]
    public async Task Liberar_hace_POST_a_la_ruta_del_pedido_con_token()
    {
        var (client, api, _) = Stock(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await client.ReleaseAsync(OrderId, default);

        var req = Assert.Single(api.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal($"http://api/internal/v1/stock/reservations/{OrderId}/release", req.Url);
        Assert.Equal("Bearer tok-1", req.Auth);
    }

    [Fact]
    public async Task Liberar_con_error_lanza_excepcion()
    {
        var (client, _, _) = Stock(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReleaseAsync(OrderId, default));
    }

    [Fact]
    public async Task Varias_llamadas_reutilizan_el_mismo_token()
    {
        var (client, api, keycloak) = Stock(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await client.ReserveAsync(OrderId, Lines, default);
        await client.ReleaseAsync(OrderId, default);
        Assert.Single(keycloak.Requests);
        Assert.True(api.Requests.All(r => r.Auth == "Bearer tok-1"));
    }

    // ---- ServiceTokenProvider ----
    [Fact]
    public async Task Pide_el_token_con_client_credentials()
    {
        var (kc, provider, _) = Tokens();
        Assert.Equal("tok-1", await provider.GetTokenAsync(default));

        var req = Assert.Single(kc.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://kc/token", req.Url);
        Assert.Contains("grant_type=client_credentials", req.Body);
        Assert.Contains("client_id=orders-worker", req.Body);
        Assert.Contains("client_secret=secreto", req.Body);
    }

    [Fact]
    public async Task Guarda_el_token_hasta_poco_antes_de_que_venza()
    {
        var (kc, provider, clock) = Tokens(expiresIn: 300);
        await provider.GetTokenAsync(default);
        clock.Advance(TimeSpan.FromSeconds(200));
        Assert.Equal("tok-1", await provider.GetTokenAsync(default));   // sigue vigente
        Assert.Single(kc.Requests);

        clock.Advance(TimeSpan.FromSeconds(80));                          // 280 s: dentro del margen de 30 s
        Assert.Equal("tok-2", await provider.GetTokenAsync(default));    // se renovo
        Assert.Equal(2, kc.Requests.Count);
    }

    [Fact]
    public async Task Si_Keycloak_falla_lanza_excepcion()
    {
        var (_, provider, _) = Tokens(code: HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetTokenAsync(default));
    }
}
