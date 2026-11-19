using MercadoYa.Modules.Catalogo;
using Xunit;

namespace MercadoYa.Tests;

public class ProductRulesTests
{
    private static CreateProductRequest Valid() => new("Yerba 1 kg", "Con palo", 480000, 1, null, 10);

    [Fact] public void Producto_valido_no_tiene_errores() =>
        Assert.Empty(ProductRules.ValidateCreate(Valid()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nombre_es_obligatorio(string? name) =>
        Assert.Contains("name", ProductRules.ValidateCreate(Valid() with { Name = name }).Keys);

    [Fact] public void Nombre_no_supera_150_caracteres() =>
        Assert.Contains("name", ProductRules.ValidateCreate(Valid() with { Name = new string('a', 151) }).Keys);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Precio_debe_ser_positivo(long price) =>
        Assert.Contains("priceCents", ProductRules.ValidateCreate(Valid() with { PriceCents = price }).Keys);

    [Fact] public void Categoria_es_obligatoria() =>
        Assert.Contains("categoryId", ProductRules.ValidateCreate(Valid() with { CategoryId = 0 }).Keys);

    [Fact] public void Stock_inicial_no_puede_ser_negativo() =>
        Assert.Contains("initialStock", ProductRules.ValidateCreate(Valid() with { InitialStock = -1 }).Keys);

    [Theory]
    [InlineData("ftp://x.com/a.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("imagen.png")]
    public void Imagen_debe_ser_url_http(string url) =>
        Assert.Contains("imageUrl", ProductRules.ValidateCreate(Valid() with { ImageUrl = url }).Keys);

    [Fact] public void Imagen_https_es_valida() =>
        Assert.Empty(ProductRules.ValidateCreate(Valid() with { ImageUrl = "https://cdn.example.com/yerba.png" }));

    [Fact]
    public void Apply_normaliza_espacios_y_url_vacia()
    {
        var p = new Product();
        ProductRules.Apply(p, "  Yerba  ", null, 100, 2, "   ");
        Assert.Equal("Yerba", p.Name);
        Assert.Equal("", p.Description);
        Assert.Null(p.ImageUrl);
        Assert.Equal(2, p.CategoryId);
    }
}
