using MercadoYa.Modules.Catalogo;
using Xunit;

namespace MercadoYa.Tests;

public class SearchTextTests
{
    [Theory]
    [InlineData("Café", "cafe")]
    [InlineData("CAFÉ MOLIDO", "cafe molido")]
    [InlineData("Azúcar, té y limón", "azucar, te y limon")]
    [InlineData("Pingüino", "pinguino")]
    [InlineData("Ñandú", "ñandu")]            // la ñ es otra letra: se mantiene
    [InlineData("yerba", "yerba")]
    [InlineData("", "")]
    public void Fold_pasa_a_minusculas_y_quita_las_tildes(string text, string expected) =>
        Assert.Equal(expected, SearchText.Fold(text));

    [Fact]
    public void Las_tablas_de_reemplazo_tienen_el_mismo_largo() =>
        Assert.Equal(SearchText.Accented.Length, SearchText.Plain.Length);

    [Fact]
    public void Translate_solo_funciona_dentro_de_una_consulta() =>
        Assert.Throws<NotSupportedException>(() => SearchText.Translate("a", "b", "c"));
}
