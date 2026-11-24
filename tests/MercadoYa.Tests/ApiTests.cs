using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MercadoYa.Api;
using MercadoYa.Modules.Catalogo;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace MercadoYa.Tests;

/// La API completa (Program real: versionado, auth, cache, health, ProblemDetails) en memoria, contra la BD mercadoya real.
/// Keycloak se reemplaza por una clave de firma del test (igual que en PaymentsHost).
public sealed class ApiHost(string connectionString) : WebApplicationFactory<CatalogProductLookup>
{
    private const string Issuer = "http://localhost:8081/realms/mercadoya";
    private readonly SymmetricSecurityKey _key = new(RandomNumberGenerator.GetBytes(32));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Mercadoya", connectionString);
        builder.ConfigureTestServices(s => s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.Authority = null;
            o.ConfigurationManager = null;
            o.TokenValidationParameters.IssuerSigningKey = _key;
        }));
    }

    /// Token como los de Keycloak: azp = client, realm_access.roles = roles del usuario.
    public string Token(string azp = "web", params string[] roles) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "mercadoya-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["azp"] = azp,
                ["sub"] = Guid.NewGuid().ToString(),
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = roles },
            },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });

    public HttpClient Client(string? token = null)
    {
        var http = CreateClient();
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
}

[Collection("postgres")]
[Trait("Category", "Integracion")]
public sealed class ApiTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiHost _api = new(pg.ConnectionString);

    public void Dispose() => _api.Dispose();

    private static string CacheOf(HttpResponseMessage r) => r.Headers.TryGetValues("X-Cache", out var v) ? v.Single() : "(sin header)";

    private async Task<(int Stock, string Cache)> PublicStockAsync(HttpClient http, int productId)
    {
        var r = await http.GetAsync($"/api/v1/products/{productId}");
        r.EnsureSuccessStatusCode();
        return ((await r.Content.ReadFromJsonAsync<ProductDto>())!.Stock, CacheOf(r));
    }

    // ---- cache del catalogo ----

    [Fact]
    public async Task Listado_y_detalle_se_cachean_y_lo_informan_en_X_Cache()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var http = _api.Client();

        Assert.Equal((10, "MISS"), await PublicStockAsync(http, p));
        Assert.Equal((10, "HIT"), await PublicStockAsync(http, p));
        Assert.Equal("MISS", CacheOf(await http.GetAsync("/api/v1/categories")));
        Assert.Equal("HIT", CacheOf(await http.GetAsync("/api/v1/categories")));
    }

    [Fact]
    public async Task Una_reserva_de_stock_del_worker_invalida_la_cache()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var http = _api.Client();
        await PublicStockAsync(http, p);
        Assert.Equal((10, "HIT"), await PublicStockAsync(http, p));

        // El worker reserva por el endpoint interno (token de servicio de orders-worker).
        var reserve = await _api.Client(_api.Token(azp: "orders-worker")).PostAsJsonAsync("/internal/v1/stock/reservations",
            new { orderId = Guid.NewGuid(), items = new[] { new { productId = p, quantity = 3 } } });
        reserve.EnsureSuccessStatusCode();

        Assert.Equal((7, "MISS"), await PublicStockAsync(http, p));   // sin invalidar, la tienda seguiria mostrando 10
    }

    [Fact]
    public async Task La_liberacion_de_stock_invalida_la_cache()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var worker = _api.Client(_api.Token(azp: "orders-worker"));
        var order = Guid.NewGuid();
        (await worker.PostAsJsonAsync("/internal/v1/stock/reservations", new { orderId = order, items = new[] { new { productId = p, quantity = 4 } } })).EnsureSuccessStatusCode();
        var http = _api.Client();
        await PublicStockAsync(http, p);

        (await worker.PostAsync($"/internal/v1/stock/reservations/{order}/release", null)).EnsureSuccessStatusCode();

        Assert.Equal((10, "MISS"), await PublicStockAsync(http, p));
    }

    [Fact]
    public async Task El_ajuste_de_stock_del_admin_invalida_la_cache()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var http = _api.Client();
        await PublicStockAsync(http, p);

        var adjust = await _api.Client(_api.Token(roles: "admin")).PatchAsJsonAsync($"/api/v1/admin/products/{p}/stock", new { delta = 5 });
        adjust.EnsureSuccessStatusCode();

        Assert.Equal((15, "MISS"), await PublicStockAsync(http, p));
    }

    [Fact]
    public async Task La_edicion_y_la_baja_del_admin_invalidan_la_cache()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var http = _api.Client();
        var admin = _api.Client(_api.Token(roles: "admin"));
        var before = await (await http.GetAsync($"/api/v1/products/{p}")).Content.ReadFromJsonAsync<ProductDto>();

        (await admin.PutAsJsonAsync($"/api/v1/admin/products/{p}", new
        {
            name = "Nombre editado", description = "", priceCents = 123400, categoryId = before!.CategoryId, imageUrl = (string?)null, active = true,
        })).EnsureSuccessStatusCode();
        var edited = await http.GetAsync($"/api/v1/products/{p}");
        var dto = (await edited.Content.ReadFromJsonAsync<ProductDto>())!;
        Assert.Equal(("MISS", "Nombre editado", 123400L), (CacheOf(edited), dto.Name, dto.PriceCents));

        (await admin.DeleteAsync($"/api/v1/admin/products/{p}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v1/products/{p}")).StatusCode);
    }

    [Fact]
    public async Task Un_cambio_hecho_por_fuera_de_la_API_no_se_ve_hasta_que_vence_la_cache()
    {
        // Limite conocido (documentado en CatalogCache): solo invalidan los cambios que pasan por la API. Un UPDATE a mano
        // en la base se ve recien cuando vence el TTL (60 s) o con el proximo cambio hecho por la API.
        var p = await pg.NewProductAsync(stock: 10);
        var http = _api.Client();
        await PublicStockAsync(http, p);

        await using (var db = pg.NewContext())
            await db.Database.ExecuteSqlAsync($"""UPDATE catalogo."Product" SET "Stock" = 99 WHERE "Id" = {p}""");

        Assert.Equal((10, "HIT"), await PublicStockAsync(http, p));
    }

    [Fact]
    public async Task Busqueda_demasiado_larga_responde_400()
    {
        var r = await _api.Client().GetAsync($"/api/v1/products?search={new string('a', CatalogoModule.MaxSearchLength + 1)}");

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---- versionado ----

    [Fact]
    public async Task Las_respuestas_informan_las_versiones_soportadas()
    {
        var r = await _api.Client().GetAsync("/api/v1/categories");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("1.0", r.Headers.GetValues("api-supported-versions").Single());
    }

    [Fact]
    public async Task La_version_tambien_se_puede_escribir_completa()
    {
        Assert.Equal(HttpStatusCode.OK, (await _api.Client().GetAsync("/api/v1.0/categories")).StatusCode);
    }

    [Fact]
    public async Task Una_version_que_no_existe_responde_404_con_ProblemDetails()
    {
        var r = await _api.Client().GetAsync("/api/v2/products");

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    // ---- errores con formato ProblemDetails ----

    [Theory]
    [InlineData("GET", "/api/v1/products/999999999", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/v1/admin/products", HttpStatusCode.Unauthorized)]
    [InlineData("DELETE", "/api/v1/categories", HttpStatusCode.MethodNotAllowed)]
    public async Task Los_errores_sin_cuerpo_salen_como_ProblemDetails(string method, string url, HttpStatusCode expected)
    {
        var r = await _api.Client().SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(expected, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    // ---- health checks ----

    [Fact]
    public async Task Health_live_y_ready_responden_JSON_con_el_estado_de_cada_check()
    {
        var http = _api.Client();

        var live = await http.GetAsync("/health/live");
        var ready = await http.GetAsync("/health/ready");
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        var check = Assert.Single(body.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal(("mercadoya-db", "Healthy"), (check.GetProperty("name").GetString(), check.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Con_la_base_caida_ready_da_503_y_dice_que_check_falla_pero_live_sigue_200()
    {
        using var caida = new ApiHost("Host=127.0.0.1;Port=1;Database=mercadoya;Username=x;Password=x;Timeout=2");
        var http = caida.Client();

        var ready = await http.GetAsync("/health/ready");
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var check = Assert.Single(body.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal(("mercadoya-db", "Unhealthy"), (check.GetProperty("name").GetString(), check.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
    }
}
