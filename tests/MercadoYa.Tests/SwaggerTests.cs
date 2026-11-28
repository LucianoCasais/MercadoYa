using System.Net;
using System.Text.Json;
using Xunit;

namespace MercadoYa.Tests;

/// La documentacion OpenAPI (Swagger) que publica la API: /swagger/v1/swagger.json. Se recorren TODAS las operaciones del
/// documento, no una lista escrita a mano: un endpoint nuevo sin resumen, sin tag o sin su seguridad hace fallar el test.
[Collection("postgres")]
[Trait("Category", "Integracion")]
public sealed class SwaggerTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiHost _api = new(pg.ConnectionString);

    public void Dispose() => _api.Dispose();

    private static readonly string[] Verbs = ["get", "post", "put", "patch", "delete"];
    private static readonly string[] Tags = ["Catálogo", "Pedidos", "Admin - Productos", "Admin - Pedidos", "Interno - Stock"];

    private async Task<JsonElement> SpecAsync()
    {
        var res = await _api.Client().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private static List<(string Verb, string Path, JsonElement Op)> Operations(JsonElement spec) =>
        spec.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Where(o => Verbs.Contains(o.Name)).Select(o => (o.Name.ToUpperInvariant(), p.Name, o.Value)))
            .ToList();

    /// Las unicas lecturas sin autenticacion de la API: el catalogo.
    private static bool IsPublic(string verb, string path) =>
        verb == "GET" && path is "/api/v1/products" or "/api/v1/products/{id}" or "/api/v1/categories";

    [Fact]
    public async Task El_documento_declara_el_esquema_de_seguridad_Bearer_y_la_info_de_la_API()
    {
        var spec = await SpecAsync();
        var bearer = spec.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());
        Assert.Equal("MercadoYa API", spec.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", spec.GetProperty("info").GetProperty("version").GetString());
    }

    [Fact]
    public async Task Solo_los_endpoints_protegidos_llevan_el_candado_y_las_respuestas_401_y_403()
    {
        var problemas = new List<string>();
        foreach (var (verb, path, op) in Operations(await SpecAsync()))
        {
            var candado = op.TryGetProperty("security", out var sec) && sec.GetArrayLength() > 0;
            var responses = op.GetProperty("responses");
            var tiene401y403 = responses.TryGetProperty("401", out _) && responses.TryGetProperty("403", out _);
            if (IsPublic(verb, path))
            {
                if (candado || tiene401y403) problemas.Add($"{verb} {path} es publico y figura como protegido");
            }
            else if (!candado || !tiene401y403) problemas.Add($"{verb} {path} es protegido y no figura con candado, 401 y 403");
        }
        Assert.Empty(problemas);
    }

    [Fact]
    public async Task Cada_operacion_tiene_resumen_tag_y_una_respuesta_exitosa()
    {
        var problemas = new List<string>();
        var operaciones = Operations(await SpecAsync());
        Assert.True(operaciones.Count >= 16, $"Se esperaban al menos 16 operaciones y hay {operaciones.Count}.");
        foreach (var (verb, path, op) in operaciones)
        {
            if (!op.TryGetProperty("summary", out var summary) || string.IsNullOrWhiteSpace(summary.GetString()))
                problemas.Add($"{verb} {path}: falta el resumen");
            var tags = op.TryGetProperty("tags", out var t) ? t.EnumerateArray().Select(x => x.GetString()).ToList() : new List<string?>();
            if (tags.Count == 0 || tags.Any(x => !Tags.Contains(x)))
                problemas.Add($"{verb} {path}: tag ausente o desconocido ({string.Join(", ", tags)})");
            if (!op.GetProperty("responses").EnumerateObject().Any(r => r.Name.StartsWith('2')))
                problemas.Add($"{verb} {path}: no declara una respuesta exitosa");
        }
        Assert.Empty(problemas);
    }

    [Fact]
    public async Task POST_orders_documenta_el_header_Idempotency_Key_y_sus_respuestas()
    {
        var op = (await SpecAsync()).GetProperty("paths").GetProperty("/api/v1/orders").GetProperty("post");
        var header = op.GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.Equal("header", header.GetProperty("in").GetString());
        Assert.True(header.GetProperty("required").GetBoolean());
        var codes = op.GetProperty("responses").EnumerateObject().Select(r => r.Name).ToList();
        foreach (var esperado in new[] { "200", "202", "400", "401", "403", "503" }) Assert.Contains(esperado, codes);
    }

    [Fact]
    public async Task Las_respuestas_exitosas_de_los_listados_describen_su_esquema()
    {
        var paths = (await SpecAsync()).GetProperty("paths");
        foreach (var path in new[] { "/api/v1/products", "/api/v1/admin/products", "/api/v1/admin/orders" })
        {
            var ok = paths.GetProperty(path).GetProperty("get").GetProperty("responses").GetProperty("200");
            Assert.True(ok.GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _), $"{path}: la respuesta 200 no tiene esquema");
        }
    }
}
