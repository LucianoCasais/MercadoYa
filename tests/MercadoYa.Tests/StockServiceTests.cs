using MercadoYa.Modules.Inventario;
using Xunit;

namespace MercadoYa.Tests;

/// StockService contra PostgreSQL real: la atomicidad depende del SQL (UPDATE ... WHERE "Stock" >= n, CHECK "Stock" >= 0),
/// asi que una base en memoria no la probaria. Los tests de concurrencia (dos compras del ultimo stock) son del dia 7.
[Collection("postgres")]
[Trait("Category", "Integracion")]
public sealed class StockServiceTests(PostgresFixture pg)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private async Task<ReserveResult> ReserveAsync(Guid orderId, params (int ProductId, int Quantity)[] items)
    {
        await using var db = pg.NewContext();
        return await new StockService(db).ReserveAsync(orderId, items.Select(i => new StockItem(i.ProductId, i.Quantity)).ToList(), Ct);
    }

    private async Task ReleaseAsync(Guid orderId)
    {
        await using var db = pg.NewContext();
        await new StockService(db).ReleaseAsync(orderId, "Liberacion", Ct);
    }

    private async Task<AdjustResult> AdjustAsync(int productId, int delta)
    {
        await using var db = pg.NewContext();
        return await new StockService(db).AdjustAsync(productId, delta, Ct);
    }

    // ---- Reserva ----

    [Fact]
    public async Task Reservar_descuenta_el_stock_y_registra_reserva_y_movimiento_de_venta()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var order = Guid.NewGuid();

        Assert.Equal(ReserveResult.Reserved, await ReserveAsync(order, (p, 3)));

        Assert.Equal(7, await pg.StockOfAsync(p));
        var r = Assert.Single(await pg.ReservationsOfAsync(order));
        Assert.Equal((p, 3, "Reservada"), (r.ProductId, r.Quantity, r.Status));
        Assert.Contains((-3, "Venta", (Guid?)order), await pg.MovementsOfAsync(p));
    }

    [Fact]
    public async Task Reservar_todo_el_stock_lo_deja_en_cero_y_el_siguiente_pedido_queda_sin_stock()
    {
        var p = await pg.NewProductAsync(stock: 2);

        Assert.Equal(ReserveResult.Reserved, await ReserveAsync(Guid.NewGuid(), (p, 2)));
        Assert.Equal(ReserveResult.InsufficientStock, await ReserveAsync(Guid.NewGuid(), (p, 1)));
        Assert.Equal(0, await pg.StockOfAsync(p));
    }

    [Fact]
    public async Task Reserva_de_varios_productos_es_todo_o_nada()
    {
        var conStock = await pg.NewProductAsync(stock: 5);
        var sinStock = await pg.NewProductAsync(stock: 1);
        var order = Guid.NewGuid();

        Assert.Equal(ReserveResult.InsufficientStock, await ReserveAsync(order, (conStock, 2), (sinStock, 2)));

        // El primer producto alcanzaba, pero la transaccion se deshace entera.
        Assert.Equal(5, await pg.StockOfAsync(conStock));
        Assert.Equal(1, await pg.StockOfAsync(sinStock));
        Assert.Empty(await pg.ReservationsOfAsync(order));
        Assert.DoesNotContain(await pg.MovementsOfAsync(conStock), m => m.Reason == "Venta");
    }

    [Fact]
    public async Task Reservar_dos_veces_el_mismo_pedido_descuenta_una_sola_vez()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var order = Guid.NewGuid();

        Assert.Equal(ReserveResult.Reserved, await ReserveAsync(order, (p, 4)));
        Assert.Equal(ReserveResult.AlreadyReserved, await ReserveAsync(order, (p, 4)));   // mensaje duplicado / reintento

        Assert.Equal(6, await pg.StockOfAsync(p));
        Assert.Single(await pg.ReservationsOfAsync(order));
    }

    [Fact]
    public async Task Producto_dado_de_baja_no_se_puede_reservar()
    {
        var p = await pg.NewProductAsync(stock: 10, active: false);

        Assert.Equal(ReserveResult.InsufficientStock, await ReserveAsync(Guid.NewGuid(), (p, 1)));
        Assert.Equal(10, await pg.StockOfAsync(p));
    }

    [Fact]
    public async Task Producto_inexistente_no_se_puede_reservar()
    {
        Assert.Equal(ReserveResult.InsufficientStock, await ReserveAsync(Guid.NewGuid(), (int.MaxValue, 1)));
    }

    // ---- Liberacion (compensacion) ----

    [Fact]
    public async Task Liberar_devuelve_el_stock_y_marca_la_reserva_como_liberada()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var order = Guid.NewGuid();
        await ReserveAsync(order, (p, 3));

        await ReleaseAsync(order);

        Assert.Equal(10, await pg.StockOfAsync(p));
        Assert.Equal("Liberada", Assert.Single(await pg.ReservationsOfAsync(order)).Status);
        Assert.Contains((3, "Liberacion", (Guid?)order), await pg.MovementsOfAsync(p));
    }

    [Fact]
    public async Task Liberar_dos_veces_devuelve_el_stock_una_sola_vez()
    {
        var p = await pg.NewProductAsync(stock: 10);
        var order = Guid.NewGuid();
        await ReserveAsync(order, (p, 3));

        await ReleaseAsync(order);
        await ReleaseAsync(order);   // compensacion repetida (reintento del worker o del admin)

        Assert.Equal(10, await pg.StockOfAsync(p));
        Assert.Single(await pg.MovementsOfAsync(p), m => m.Reason == "Liberacion");
    }

    [Fact]
    public async Task Liberar_un_pedido_sin_reserva_no_hace_nada()
    {
        var p = await pg.NewProductAsync(stock: 10);

        await ReleaseAsync(Guid.NewGuid());

        Assert.Equal(10, await pg.StockOfAsync(p));
    }

    // ---- Ajuste manual del admin ----

    [Fact]
    public async Task Ajuste_positivo_y_negativo_cambia_el_stock_y_registra_movimientos()
    {
        var p = await pg.NewProductAsync(stock: 5);

        var up = await AdjustAsync(p, 10);
        var down = await AdjustAsync(p, -4);

        Assert.Equal((AdjustOutcome.Adjusted, 15), (up.Outcome, up.Stock));
        Assert.Equal((AdjustOutcome.Adjusted, 11), (down.Outcome, down.Stock));
        Assert.Equal(11, await pg.StockOfAsync(p));
        var ajustes = (await pg.MovementsOfAsync(p)).Where(m => m.Reason == "Ajuste").Select(m => m.Delta);
        Assert.Equal([10, -4], ajustes);
    }

    [Fact]
    public async Task Ajuste_que_dejaria_stock_negativo_se_rechaza_sin_cambios()
    {
        var p = await pg.NewProductAsync(stock: 3);

        Assert.Equal(AdjustOutcome.WouldBeNegative, (await AdjustAsync(p, -4)).Outcome);

        Assert.Equal(3, await pg.StockOfAsync(p));
        Assert.DoesNotContain(await pg.MovementsOfAsync(p), m => m.Reason == "Ajuste");
    }

    [Fact]
    public async Task Ajuste_que_deja_el_stock_en_cero_es_valido()
    {
        var p = await pg.NewProductAsync(stock: 3);

        var r = await AdjustAsync(p, -3);

        Assert.Equal((AdjustOutcome.Adjusted, 0), (r.Outcome, r.Stock));
    }

    [Fact]
    public async Task Ajuste_de_producto_inexistente_devuelve_NotFound()
    {
        Assert.Equal(AdjustOutcome.NotFound, (await AdjustAsync(int.MaxValue, 5)).Outcome);
    }

    [Fact]
    public async Task Ajuste_de_un_producto_inactivo_esta_permitido()
    {
        var p = await pg.NewProductAsync(stock: 0, active: false);

        Assert.Equal((AdjustOutcome.Adjusted, 8), ((await AdjustAsync(p, 8)).Outcome, (await pg.StockOfAsync(p))));
    }
}
