using MercadoYa.Modules.Inventario;
using Xunit;

namespace MercadoYa.Tests;

/// Concurrencia de stock contra PostgreSQL real (dia 7): muchas compras a la vez sobre el mismo producto.
/// Cada operacion usa su propio DbContext y conexion (como cada request de la API), y todas arrancan a la vez.
[Collection("postgres")]
[Trait("Category", "Integracion")]
public sealed class StockConcurrencyTests(PostgresFixture pg)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private async Task<ReserveResult> ReserveAsync(Guid orderId, int productId, int quantity)
    {
        await using var db = pg.NewContext();
        return await new StockService(db).ReserveAsync(orderId, [new StockItem(productId, quantity)], Ct);
    }

    private async Task<AdjustOutcome> AdjustAsync(int productId, int delta)
    {
        await using var db = pg.NewContext();
        return (await new StockService(db).AdjustAsync(productId, delta, Ct)).Outcome;
    }

    /// Lanza todas las tareas en hilos distintos y las libera juntas para que compitan de verdad.
    private static async Task<T[]> RaceAsync<T>(IEnumerable<Func<Task<T>>> work)
    {
        var start = new TaskCompletionSource();
        var tasks = work.Select(w => Task.Run(async () => { await start.Task; return await w(); })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Dos_compras_del_ultimo_stock_solo_una_gana()
    {
        var p = await pg.NewProductAsync(stock: 1);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var results = await RaceAsync([() => ReserveAsync(a, p, 1), () => ReserveAsync(b, p, 1)]);

        Assert.Equal(1, results.Count(r => r == ReserveResult.Reserved));
        Assert.Equal(1, results.Count(r => r == ReserveResult.InsufficientStock));
        Assert.Equal(0, await pg.StockOfAsync(p));
        // Solo la ganadora deja reserva y movimiento de venta.
        var reservas = (await pg.ReservationsOfAsync(a)).Count + (await pg.ReservationsOfAsync(b)).Count;
        Assert.Equal(1, reservas);
        Assert.Equal(1, (await pg.MovementsOfAsync(p)).Count(m => m.Reason == "Venta"));
    }

    [Fact]
    public async Task Veinte_compras_sobre_cinco_unidades_confirman_exactamente_cinco_y_nunca_hay_stock_negativo()
    {
        var p = await pg.NewProductAsync(stock: 5);

        var results = await RaceAsync(Enumerable.Range(0, 20).Select<int, Func<Task<ReserveResult>>>(_ => () => ReserveAsync(Guid.NewGuid(), p, 1)));

        Assert.Equal(5, results.Count(r => r == ReserveResult.Reserved));
        Assert.Equal(15, results.Count(r => r == ReserveResult.InsufficientStock));
        Assert.Equal(0, await pg.StockOfAsync(p));
        Assert.Equal(5, (await pg.MovementsOfAsync(p)).Count(m => m.Reason == "Venta"));
    }

    [Fact]
    public async Task Pedidos_de_varias_unidades_no_sobrevenden_el_stock()
    {
        var p = await pg.NewProductAsync(stock: 10);

        // 6 pedidos de 3 unidades = 18 pedidas sobre 10: caben 3 pedidos (9 unidades) y sobra 1.
        var results = await RaceAsync(Enumerable.Range(0, 6).Select<int, Func<Task<ReserveResult>>>(_ => () => ReserveAsync(Guid.NewGuid(), p, 3)));

        Assert.Equal(3, results.Count(r => r == ReserveResult.Reserved));
        Assert.Equal(3, results.Count(r => r == ReserveResult.InsufficientStock));
        Assert.Equal(1, await pg.StockOfAsync(p));
    }

    [Fact]
    public async Task El_mismo_pedido_enviado_varias_veces_a_la_vez_descuenta_una_sola_vez()
    {
        var p = await pg.NewProductAsync(stock: 100);
        var order = Guid.NewGuid();

        var results = await RaceAsync(Enumerable.Range(0, 6).Select<int, Func<Task<ReserveResult>>>(_ => () => ReserveAsync(order, p, 3)));

        Assert.Equal(1, results.Count(r => r == ReserveResult.Reserved));
        Assert.Equal(5, results.Count(r => r == ReserveResult.AlreadyReserved));
        Assert.Equal(97, await pg.StockOfAsync(p));
        Assert.Single(await pg.ReservationsOfAsync(order));
        Assert.Equal(1, (await pg.MovementsOfAsync(p)).Count(m => m.Reason == "Venta"));
    }

    [Fact]
    public async Task El_mismo_pedido_a_la_vez_con_stock_justo_no_se_informa_como_falta_de_stock()
    {
        // Antes del arreglo, las copias perdedoras veian "Stock >= n" falso (la ganadora ya lo descontó) y devolvian
        // InsufficientStock: el worker habria rechazado por SinStock un pedido que ya estaba reservado.
        var p = await pg.NewProductAsync(stock: 3);
        var order = Guid.NewGuid();

        var results = await RaceAsync(Enumerable.Range(0, 6).Select<int, Func<Task<ReserveResult>>>(_ => () => ReserveAsync(order, p, 3)));

        Assert.Equal(1, results.Count(r => r == ReserveResult.Reserved));
        Assert.DoesNotContain(ReserveResult.InsufficientStock, results);
        Assert.Equal(0, await pg.StockOfAsync(p));
        Assert.Single(await pg.ReservationsOfAsync(order));
    }

    [Fact]
    public async Task Compras_y_bajas_manuales_a_la_vez_nunca_dejan_el_stock_negativo()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var work = Enumerable.Range(0, 10).Select<int, Func<Task<string>>>(_ => async () => (await ReserveAsync(Guid.NewGuid(), p, 1)).ToString())
            .Concat(Enumerable.Range(0, 10).Select<int, Func<Task<string>>>(_ => async () => (await AdjustAsync(p, -1)).ToString()));

        var results = await RaceAsync(work);

        // 20 intentos de sacar 1 unidad sobre 10 disponibles: exactamente 10 salen bien (entre ventas y bajas), el resto falla.
        var ok = results.Count(r => r is nameof(ReserveResult.Reserved) or nameof(AdjustOutcome.Adjusted));
        Assert.Equal(10, ok);
        Assert.Equal(0, await pg.StockOfAsync(p));
        var movimientos = await pg.MovementsOfAsync(p);
        Assert.Equal(10, movimientos.Count(m => m.Reason is "Venta" or "Ajuste" && m.Delta == -1));
    }

    [Fact]
    public async Task Reserva_y_liberacion_simultaneas_del_mismo_pedido_dejan_el_stock_consistente()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var order = Guid.NewGuid();
        Assert.Equal(ReserveResult.Reserved, await ReserveAsync(order, p, 4));

        // Dos liberaciones a la vez (cancelacion del admin + compensacion del worker): el stock vuelve una sola vez.
        await RaceAsync(Enumerable.Range(0, 2).Select<int, Func<Task<int>>>(_ => async () =>
        {
            await using var db = pg.NewContext();
            await new StockService(db).ReleaseAsync(order, "Liberacion", Ct);
            return 0;
        }));

        Assert.Equal(10, await pg.StockOfAsync(p));
        Assert.Equal(1, (await pg.MovementsOfAsync(p)).Count(m => m.Reason == "Liberacion"));
    }
}
