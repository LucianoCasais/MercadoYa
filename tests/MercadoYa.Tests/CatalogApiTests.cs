using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MercadoYa.Modules.Catalogo;
using MercadoYa.Modules.Inventario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MercadoYa.Tests;

/// Catalogo por la API real (rutas, validaciones, codigos HTTP) contra PostgreSQL. Cada test crea sus productos con un
/// nombre unico y busca por ese nombre, asi no depende de lo que hayan creado otros tests en la misma base.
[Collection("postgres")]
[Trait("Category", "Integracion")]
public sealed class CatalogApiTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiHost _api = new(pg.ConnectionString);
    private readonly string _tag = "t" + Guid.NewGuid().ToString("N")[..10];   // marca unica de este test en los nombres

    public void Dispose() => _api.Dispose();

    private HttpClient Admin => _api.Client(_api.Token("web", "admin"));
    private HttpClient Public => _api.Client();

    private async Task<int> CategoryIdAsync()
    {
        await using var db = pg.NewContext();
        return await db.Database.SqlQuery<int>($"""SELECT "Id" AS "Value" FROM catalogo."Category" ORDER BY "Id" LIMIT 1""").SingleAsync();
    }

    private async Task<AdminProductDto> CreateAsync(string name, long priceCents = 100000, int stock = 10)
    {
        var r = await Admin.PostAsJsonAsync("/api/v1/admin/products",
            new { name = $"{name} {_tag}", description = "desc", priceCents, categoryId = await CategoryIdAsync(), imageUrl = (string?)null, initialStock = stock });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<AdminProductDto>())!;
    }

    private async Task<PagedResult<ProductDto>> SearchAsync(string search, int page = 1, int pageSize = 12) =>
        (await Public.GetFromJsonAsync<PagedResult<ProductDto>>($"/api/v1/products?search={Uri.EscapeDataString(search)}&page={page}&pageSize={pageSize}"))!;

    private static async Task<Dictionary<string, string[]>> ErrorsOf(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }

    // ---- lectura publica ----

    [Fact]
    public async Task La_busqueda_no_distingue_mayusculas_y_encuentra_por_parte_del_nombre()
    {
        await CreateAsync("Yerba Suave");
        await CreateAsync("Cafe Fuerte");

        var r = await SearchAsync($"suave {_tag}".ToUpperInvariant());   // "SUAVE T..." encuentra "Yerba Suave t..."

        Assert.Equal($"Yerba Suave {_tag}", Assert.Single(r.Items).Name);
    }

    [Fact]
    public async Task El_listado_pagina_y_ordena_por_nombre()
    {
        foreach (var n in new[] { "C", "A", "E", "B", "D" }) await CreateAsync($"Prod {n}");

        var first = await SearchAsync(_tag, page: 1, pageSize: 2);
        var third = await SearchAsync(_tag, page: 3, pageSize: 2);
        var beyond = await SearchAsync(_tag, page: 4, pageSize: 2);

        Assert.Equal(5, first.Total);
        Assert.Equal(["Prod A", "Prod B"], first.Items.Select(i => i.Name.Split(' ')[0] + " " + i.Name.Split(' ')[1]));
        Assert.Equal("Prod E", string.Join(' ', Assert.Single(third.Items).Name.Split(' ')[..2]));
        Assert.Empty(beyond.Items);
    }

    [Fact]
    public async Task Un_producto_dado_de_baja_no_aparece_en_la_tienda_pero_si_en_el_admin()
    {
        var p = await CreateAsync("Oculto");
        (await Admin.DeleteAsync($"/api/v1/admin/products/{p.Id}")).EnsureSuccessStatusCode();

        Assert.Empty((await SearchAsync(_tag)).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await Public.GetAsync($"/api/v1/products/{p.Id}")).StatusCode);
        var admin = await Admin.GetFromJsonAsync<AdminProductDto>($"/api/v1/admin/products/{p.Id}");
        Assert.False(admin!.Active);
    }

    [Fact]
    public async Task El_detalle_devuelve_los_datos_del_producto()
    {
        var p = await CreateAsync("Detalle", priceCents: 123456, stock: 7);

        var d = await Public.GetFromJsonAsync<ProductDto>($"/api/v1/products/{p.Id}");

        Assert.Equal((p.Name, 123456L, 7, "desc"), (d!.Name, d.PriceCents, d.Stock, d.Description));
    }

    // ---- ABM del admin ----

    [Fact]
    public async Task El_alta_devuelve_201_con_Location_y_registra_el_stock_inicial()
    {
        var r = await Admin.PostAsJsonAsync("/api/v1/admin/products",
            new { name = $"Alta {_tag}", description = "", priceCents = 5000, categoryId = await CategoryIdAsync(), imageUrl = "https://example.com/a.png", initialStock = 4 });
        var created = await r.Content.ReadFromJsonAsync<AdminProductDto>();

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal($"/api/v1/admin/products/{created!.Id}", r.Headers.Location!.ToString());
        Assert.Contains((4, "Inicial", (Guid?)null), await pg.MovementsOfAsync(created.Id));
    }

    [Fact]
    public async Task El_alta_sin_stock_inicial_no_registra_movimiento()
    {
        var p = await CreateAsync("Sin stock", stock: 0);

        Assert.Empty(await pg.MovementsOfAsync(p.Id));
    }

    [Fact]
    public async Task El_alta_invalida_responde_400_con_un_error_por_campo()
    {
        var r = await Admin.PostAsJsonAsync("/api/v1/admin/products",
            new { name = " ", priceCents = 0, categoryId = 0, imageUrl = "ftp://x", initialStock = -1 });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(["categoryId", "imageUrl", "initialStock", "name", "priceCents"], (await ErrorsOf(r)).Keys.Order());
    }

    [Fact]
    public async Task El_alta_con_una_categoria_que_no_existe_responde_400()
    {
        var r = await Admin.PostAsJsonAsync("/api/v1/admin/products",
            new { name = $"X {_tag}", priceCents = 100, categoryId = 999999, initialStock = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("categoryId", (await ErrorsOf(r)).Keys);
    }

    [Fact]
    public async Task La_edicion_cambia_los_datos_pero_no_el_stock()
    {
        var p = await CreateAsync("Editar", stock: 9);

        var r = await Admin.PutAsJsonAsync($"/api/v1/admin/products/{p.Id}",
            new { name = $"Editado {_tag}", description = "nueva", priceCents = 777, categoryId = p.CategoryId, imageUrl = (string?)null, active = true });
        var edited = await r.Content.ReadFromJsonAsync<AdminProductDto>();

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(($"Editado {_tag}", "nueva", 777L, 9), (edited!.Name, edited.Description, edited.PriceCents, edited.Stock));
    }

    [Fact]
    public async Task Reactivar_con_la_edicion_vuelve_a_mostrar_el_producto()
    {
        var p = await CreateAsync("Reactivar");
        await Admin.DeleteAsync($"/api/v1/admin/products/{p.Id}");

        await Admin.PutAsJsonAsync($"/api/v1/admin/products/{p.Id}",
            new { name = p.Name, description = p.Description, priceCents = p.PriceCents, categoryId = p.CategoryId, imageUrl = p.ImageUrl, active = true });

        Assert.Equal(HttpStatusCode.OK, (await Public.GetAsync($"/api/v1/products/{p.Id}")).StatusCode);
    }

    [Fact]
    public async Task La_baja_es_idempotente_y_un_producto_inexistente_da_404()
    {
        var p = await CreateAsync("Baja");

        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/admin/products/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/admin/products/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync("/api/v1/admin/products/999999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PutAsJsonAsync("/api/v1/admin/products/999999999",
            new { name = "x", priceCents = 1, categoryId = p.CategoryId, active = true })).StatusCode);
    }

    [Fact]
    public async Task El_listado_del_admin_incluye_los_inactivos()
    {
        var activo = await CreateAsync("Activo");
        var inactivo = await CreateAsync("Inactivo");
        await Admin.DeleteAsync($"/api/v1/admin/products/{inactivo.Id}");

        var list = await Admin.GetFromJsonAsync<PagedResult<AdminProductDto>>($"/api/v1/admin/products?search={_tag}");

        Assert.Equal([activo.Id, inactivo.Id], list!.Items.Select(i => i.Id).Order());
    }

    // ---- ajuste de stock del admin ----

    [Theory]
    [InlineData(5, HttpStatusCode.OK)]
    [InlineData(-10, HttpStatusCode.OK)]
    [InlineData(-11, HttpStatusCode.Conflict)]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(100001, HttpStatusCode.BadRequest)]
    public async Task El_ajuste_de_stock_responde_segun_el_caso(int delta, HttpStatusCode expected)
    {
        var p = await CreateAsync("Ajuste", stock: 10);

        var r = await Admin.PatchAsJsonAsync($"/api/v1/admin/products/{p.Id}/stock", new { delta });

        Assert.Equal(expected, r.StatusCode);
    }

    [Fact]
    public async Task El_ajuste_de_un_producto_inexistente_da_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await Admin.PatchAsJsonAsync("/api/v1/admin/products/999999999/stock", new { delta = 1 })).StatusCode);
    }

    // ---- reserva interna (worker): datos invalidos dan 400, no 500 ----

    public static TheoryData<string, object> ReservasInvalidas => new()
    {
        { "cuerpo vacio", new { } },
        { "sin items", new { orderId = Guid.NewGuid(), items = Array.Empty<object>() } },
        { "pedido vacio", new { orderId = Guid.Empty, items = new[] { new { productId = 1, quantity = 1 } } } },
        { "cantidad 0", new { orderId = Guid.NewGuid(), items = new[] { new { productId = 1, quantity = 0 } } } },
        { "producto repetido", new { orderId = Guid.NewGuid(), items = new[] { new { productId = 1, quantity = 1 }, new { productId = 1, quantity = 2 } } } },
    };

    [Theory]
    [MemberData(nameof(ReservasInvalidas))]
    public async Task Una_reserva_invalida_responde_400(string caso, object body)
    {
        var r = await _api.Client(_api.Token("orders-worker")).PostAsJsonAsync("/internal/v1/stock/reservations", body);

        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{caso}: {(int)r.StatusCode}");
    }

    [Fact]
    public void La_validacion_de_reserva_acepta_un_pedido_correcto() =>
        Assert.Empty(InventarioModule.ValidateReserve(new ReserveRequest(Guid.NewGuid(), [new StockItem(1, 2), new StockItem(2, 1)])));
}
