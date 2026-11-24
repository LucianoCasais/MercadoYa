using MercadoYa.Modules.Catalogo;
using Xunit;

namespace MercadoYa.Tests;

public class CatalogCacheTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task La_primera_lectura_va_a_la_base_y_la_segunda_sale_de_memoria()
    {
        using var cache = new CatalogCache();
        var loads = 0;

        var first = await cache.GetOrCreateAsync("k", _ => Task.FromResult(++loads), Ct);
        var second = await cache.GetOrCreateAsync("k", _ => Task.FromResult(++loads), Ct);

        Assert.Equal((1, false), first);
        Assert.Equal((1, true), second);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Claves_distintas_se_cachean_por_separado()
    {
        using var cache = new CatalogCache();

        await cache.GetOrCreateAsync("busqueda=yerba", _ => Task.FromResult("yerba"), Ct);
        var other = await cache.GetOrCreateAsync("busqueda=cafe", _ => Task.FromResult("cafe"), Ct);

        Assert.Equal(("cafe", false), other);
    }

    [Fact]
    public async Task Un_cambio_del_catalogo_vence_todas_las_entradas()
    {
        using var cache = new CatalogCache();
        await cache.GetOrCreateAsync("a", _ => Task.FromResult(1), Ct);
        await cache.GetOrCreateAsync("b", _ => Task.FromResult(1), Ct);

        cache.CatalogChanged();

        Assert.False((await cache.GetOrCreateAsync("a", _ => Task.FromResult(2), Ct)).Hit);
        Assert.False((await cache.GetOrCreateAsync("b", _ => Task.FromResult(2), Ct)).Hit);
        Assert.Equal((2, true), await cache.GetOrCreateAsync("a", _ => Task.FromResult(3), Ct));   // la nueva generacion si se cachea
    }

    [Fact]
    public async Task Si_el_catalogo_cambia_mientras_se_lee_la_base_el_dato_viejo_no_queda_cacheado()
    {
        using var cache = new CatalogCache();

        // Lectura lenta: a mitad de camino se confirma una compra (que invalida). Lo leido ya es viejo.
        var stale = await cache.GetOrCreateAsync("stock", _ =>
        {
            cache.CatalogChanged();
            return Task.FromResult("stock viejo");
        }, Ct);
        var next = await cache.GetOrCreateAsync("stock", _ => Task.FromResult("stock nuevo"), Ct);

        Assert.Equal(("stock viejo", false), stale);   // a quien pidio se le responde lo que se leyo...
        Assert.Equal(("stock nuevo", false), next);    // ...pero no se guardo: el siguiente vuelve a la base
    }

    [Fact]
    public async Task Tambien_se_cachea_un_resultado_vacio()
    {
        using var cache = new CatalogCache();

        await cache.GetOrCreateAsync<string?>("producto|999", _ => Task.FromResult<string?>(null), Ct);
        var again = await cache.GetOrCreateAsync<string?>("producto|999", _ => Task.FromResult<string?>("no deberia leer"), Ct);

        Assert.Equal((null, true), again);
    }
}
