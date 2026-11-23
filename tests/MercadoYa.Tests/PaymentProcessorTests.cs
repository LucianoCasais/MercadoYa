extern alias payments;

using payments::MercadoYa.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MercadoYa.Tests;

public class PaymentRulesTests
{
    [Theory]
    [InlineData(999, true)]       // $ 9,99
    [InlineData(1999, true)]
    [InlineData(480000, false)]   // $ 4.800,00
    [InlineData(998, false)]
    [InlineData(9990, false)]
    public void Solo_se_rechazan_los_importes_cuyos_centavos_terminan_en_999(long cents, bool rejected) =>
        Assert.Equal(rejected, PaymentRules.IsRejected(cents));
}

/// PaymentProcessor contra la BD payments real: la idempotencia depende del indice unico de OrderId.
[Collection("payments-db")]
[Trait("Category", "Integracion")]
public sealed class PaymentProcessorTests(PaymentsDbFixture pg)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private async Task<ChargeResult> ChargeAsync(Guid orderId, long cents)
    {
        await using var db = pg.NewContext();
        return await new PaymentProcessor(db, NullLogger<PaymentProcessor>.Instance).ChargeAsync(orderId, cents, Ct);
    }

    [Fact]
    public async Task Cobro_normal_se_aprueba_y_queda_registrado()
    {
        var order = Guid.NewGuid();

        var r = await ChargeAsync(order, 480000);

        Assert.Equal((true, null, false), (r.Approved, r.Reason, r.Replayed));
        var rec = Assert.Single(await pg.RecordsOfAsync(order));
        Assert.Equal((PaymentStatuses.Aprobado, 480000L, (string?)null), (rec.Status, rec.AmountCents, rec.Reason));
    }

    [Fact]
    public async Task Importe_terminado_en_999_se_rechaza_con_motivo()
    {
        var order = Guid.NewGuid();

        var r = await ChargeAsync(order, 999);

        Assert.False(r.Approved);
        Assert.Equal(PaymentRules.RejectedReason, r.Reason);
        Assert.Equal(PaymentStatuses.Rechazado, Assert.Single(await pg.RecordsOfAsync(order)).Status);
    }

    [Fact]
    public async Task Repetir_el_cobro_devuelve_el_mismo_resultado_sin_cobrar_otra_vez()
    {
        var order = Guid.NewGuid();

        var first = await ChargeAsync(order, 250000);
        var second = await ChargeAsync(order, 250000);   // reintento del worker / mensaje duplicado

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Approved, second.Approved);
        Assert.Single(await pg.RecordsOfAsync(order));
    }

    [Fact]
    public async Task Repetir_un_cobro_rechazado_sigue_rechazado()
    {
        var order = Guid.NewGuid();
        await ChargeAsync(order, 1999);

        var again = await ChargeAsync(order, 1999);

        Assert.Equal((false, true), (again.Approved, again.Replayed));
        Assert.Equal(PaymentRules.RejectedReason, again.Reason);
    }

    [Fact]
    public async Task Mismo_pedido_con_otro_importe_es_un_conflicto_y_no_cambia_el_cobro()
    {
        var order = Guid.NewGuid();
        await ChargeAsync(order, 480000);

        var ex = await Assert.ThrowsAsync<PaymentConflictException>(() => ChargeAsync(order, 1000));

        Assert.Equal(order, ex.OrderId);
        Assert.Equal(480000, Assert.Single(await pg.RecordsOfAsync(order)).AmountCents);
    }

    [Fact]
    public async Task Veinte_cobros_simultaneos_del_mismo_pedido_registran_uno_solo()
    {
        var order = Guid.NewGuid();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Cada llamada con su propio DbContext (como requests distintas); todas esperan la largada y salen a la vez.
        var calls = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await ChargeAsync(order, 330000);
        })).ToList();
        start.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.True(r.Approved));
        Assert.Single(results, r => !r.Replayed);   // exactamente uno cobro de verdad; el resto recibio el resultado guardado
        Assert.Single(await pg.RecordsOfAsync(order));
    }

    [Fact]
    public async Task Pedido_vacio_o_importe_no_positivo_se_rechazan_sin_registrar()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ChargeAsync(Guid.Empty, 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ChargeAsync(Guid.NewGuid(), 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ChargeAsync(Guid.NewGuid(), -5));
        Assert.Empty(await pg.RecordsOfAsync(Guid.Empty));
    }
}
