using MercadoYa.Shared;
using Xunit;

namespace MercadoYa.Tests;

public class PagingTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    [InlineData(int.MaxValue, Paging.MaxPage)]   // sin el tope, (page - 1) * pageSize desborda y el OFFSET queda negativo
    public void Page_queda_entre_1_y_el_maximo(int page, int expected) =>
        Assert.Equal(expected, Paging.Normalize(page, 20, 20, 100).Page);

    [Theory]
    [InlineData(0, 12)]
    [InlineData(-1, 12)]
    [InlineData(51, 12)]
    [InlineData(50, 50)]
    [InlineData(1, 1)]
    public void PageSize_fuera_de_rango_vuelve_al_valor_por_defecto(int size, int expected) =>
        Assert.Equal(expected, Paging.Normalize(1, size, 12, 50).PageSize);

    [Fact]
    public void El_offset_de_la_ultima_pagina_no_desborda() =>
        Assert.True((Paging.MaxPage - 1) * 100 > 0);
}
