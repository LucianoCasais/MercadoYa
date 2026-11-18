using MercadoYa.Modules.Catalogo;
using Xunit;

namespace MercadoYa.Tests;

public class CategoryTreeTests
{
    private static readonly (int Id, int? ParentId)[] Tree = [(1, null), (2, 1), (3, 2), (4, 2), (5, null), (6, 5)];

    [Fact] public void Raiz_incluye_todos_sus_descendientes() =>
        Assert.Equal(new[] { 1, 2, 3, 4 }, CategoryTree.DescendantIds(Tree, 1).OrderBy(x => x));

    [Fact] public void Hoja_solo_se_incluye_a_si_misma() =>
        Assert.Equal(new[] { 3 }, CategoryTree.DescendantIds(Tree, 3));

    [Fact] public void No_mezcla_ramas() =>
        Assert.DoesNotContain(6, CategoryTree.DescendantIds(Tree, 1));
}
