using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MercadoYa.Tests;

/// Seguridad de la API (indicador del acta: "100 % de las funciones del panel de administracion restringidas al rol
/// administrador"). Se recorren TODOS los endpoints que la API tiene registrados, no una lista escrita a mano: un endpoint
/// nuevo que nadie clasifique hace fallar el test.
[Collection("postgres")]
[Trait("Category", "Integracion")]
public sealed class SecurityTests(PostgresFixture pg, ITestOutputHelper output) : IDisposable
{
    private readonly ApiHost _api = new(pg.ConnectionString);

    public void Dispose() => _api.Dispose();

    private enum Access { Public, Admin, Cliente, Worker }

    private sealed record Ep(string Method, string Url, string Pattern, Access Access);

    /// Quien puede usar cada endpoint, segun su ruta. Si aparece una ruta que no encaja, el test falla (hay que clasificarla).
    private static Access Classify(string pattern, string method) => pattern switch
    {
        _ when pattern.StartsWith("/health/") => Access.Public,
        _ when pattern.StartsWith("/internal/") => Access.Worker,
        _ when pattern.Contains("/admin/") => Access.Admin,
        _ when pattern.Contains("/orders") => Access.Cliente,
        _ when method == "GET" && (pattern.Contains("/products") || pattern.Contains("/categories")) => Access.Public,
        _ => throw new InvalidOperationException($"Endpoint sin clasificar en SecurityTests: {method} {pattern}"),
    };

    private List<Ep> Endpoints()
    {
        var source = _api.Services.GetRequiredService<EndpointDataSource>();
        var list = new List<Ep>();
        foreach (var e in source.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = "/" + e.RoutePattern.RawText!.TrimStart('/');
            // Ids que no existen: las llamadas permitidas no modifican datos (dan 404/400), solo se mide quien pasa.
            var url = Regex.Replace(pattern, @"\{(\w+)(:[^}]*)?\}", m => m.Groups[2].Value switch
            {
                ":int" => "999999999",
                ":guid" => Guid.NewGuid().ToString(),
                ":apiVersion" => "1",
                _ => "x",
            });
            foreach (var method in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                list.Add(new Ep(method, url, pattern, Classify(pattern, method)));
        }
        return list.DistinctBy(x => (x.Method, x.Pattern)).OrderBy(x => x.Pattern).ToList();
    }

    private async Task<HttpStatusCode> Call(Ep ep, string? token)
    {
        var request = new HttpRequestMessage(new HttpMethod(ep.Method), ep.Url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ep.Method is "POST" or "PUT" or "PATCH") request.Content = JsonContent.Create(new { });
        return (await _api.Client().SendAsync(request)).StatusCode;
    }

    private static bool Allowed(HttpStatusCode s) => s is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && (int)s < 500;

    [Fact]
    public async Task Cada_endpoint_exige_exactamente_el_rol_que_corresponde()
    {
        var eps = Endpoints();
        string admin = _api.Token("web", "admin"), cliente = _api.Token("web", "cliente"), worker = _api.Token("orders-worker");
        var problems = new List<string>();
        void Expect(Ep ep, string who, HttpStatusCode got, Func<HttpStatusCode, bool> ok, string expected)
        {
            if (!ok(got)) problems.Add($"{ep.Method} {ep.Pattern} [{ep.Access}] con {who}: {(int)got}, se esperaba {expected}");
        }

        foreach (var ep in eps)
        {
            output.WriteLine($"{ep.Access,-8} {ep.Method,-6} {ep.Pattern}");
            switch (ep.Access)
            {
                case Access.Public:
                    Expect(ep, "anonimo", await Call(ep, null), Allowed, "acceso libre");
                    break;
                case Access.Admin:
                    Expect(ep, "anonimo", await Call(ep, null), s => s == HttpStatusCode.Unauthorized, "401");
                    Expect(ep, "cliente", await Call(ep, cliente), s => s == HttpStatusCode.Forbidden, "403");
                    Expect(ep, "orders-worker", await Call(ep, worker), s => s == HttpStatusCode.Forbidden, "403");
                    Expect(ep, "admin", await Call(ep, admin), Allowed, "acceso permitido");
                    break;
                case Access.Worker:
                    Expect(ep, "anonimo", await Call(ep, null), s => s == HttpStatusCode.Unauthorized, "401");
                    Expect(ep, "admin", await Call(ep, admin), s => s == HttpStatusCode.Forbidden, "403");
                    Expect(ep, "cliente", await Call(ep, cliente), s => s == HttpStatusCode.Forbidden, "403");
                    Expect(ep, "orders-worker", await Call(ep, worker), Allowed, "acceso permitido");
                    break;
                case Access.Cliente:
                    Expect(ep, "anonimo", await Call(ep, null), s => s == HttpStatusCode.Unauthorized, "401");
                    Expect(ep, "admin (sin rol cliente)", await Call(ep, admin), s => s == HttpStatusCode.Forbidden, "403");
                    Expect(ep, "orders-worker", await Call(ep, worker), s => s == HttpStatusCode.Forbidden, "403");
                    Expect(ep, "cliente", await Call(ep, cliente), Allowed, "acceso permitido");
                    break;
            }
        }

        // Que el recorrido no haya quedado vacio por un cambio de rutas (un test que no prueba nada pasaria igual).
        Assert.True(eps.Count(e => e.Access == Access.Admin) >= 9, "se esperaban al menos 9 endpoints de admin");
        Assert.True(eps.Count(e => e.Access == Access.Worker) >= 2, "se esperaban los 2 endpoints internos de stock");
        Assert.True(eps.Count(e => e.Access == Access.Cliente) >= 2, "se esperaban POST /orders y GET /orders/{id}");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ---- validacion del token (firma, emisor, audiencia, vencimiento) ----

    public static TheoryData<string> TokensInvalidos => new() { "vencido", "otra-audiencia", "otro-emisor", "otra-firma", "basura" };

    [Theory]
    [MemberData(nameof(TokensInvalidos))]
    public async Task Un_token_invalido_responde_401_aunque_tenga_rol_admin(string caso)
    {
        using var otro = new ApiHost(pg.ConnectionString);   // otra clave de firma
        var token = caso switch
        {
            "vencido" => _api.RawToken("web", ["admin"], expiresInMinutes: -10),
            "otra-audiencia" => _api.RawToken("web", ["admin"], audience: "otra-api"),
            "otro-emisor" => _api.RawToken("web", ["admin"], issuer: "http://evil.example/realms/mercadoya"),
            "otra-firma" => otro.Token("web", "admin"),
            _ => "esto.no.es-un-jwt",
        };

        var r = await _api.Client(token).GetAsync("/api/v1/admin/products");

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Un_token_valido_de_admin_entra()
    {
        Assert.Equal(HttpStatusCode.OK, (await _api.Client(_api.Token("web", "admin")).GetAsync("/api/v1/admin/products")).StatusCode);
    }

    // ---- cada cliente ve solo sus pedidos (el userId sale del token, nunca del cuerpo) ----

    [Fact]
    public async Task Un_cliente_no_puede_ver_el_pedido_de_otro()
    {
        var duenio = Guid.NewGuid().ToString();
        var orderId = Guid.NewGuid();
        await using (var db = pg.NewContext())
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO pedidos."Order" ("Id","UserId","Status","TotalCents","IdempotencyKey")
                VALUES ({orderId}, {duenio}, 'Pendiente', 1000, {Guid.NewGuid().ToString()})
                """);

        var propio = await _api.Client(_api.RawToken("web", ["cliente"], sub: duenio)).GetAsync($"/api/v1/orders/{orderId}");
        var ajeno = await _api.Client(_api.RawToken("web", ["cliente"], sub: Guid.NewGuid().ToString())).GetAsync($"/api/v1/orders/{orderId}");

        Assert.Equal(HttpStatusCode.OK, propio.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ajeno.StatusCode);   // 404 y no 403: no revela que el pedido existe
    }
}
