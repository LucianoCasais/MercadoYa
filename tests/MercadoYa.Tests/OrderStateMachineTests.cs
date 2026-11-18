using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using Xunit;

namespace MercadoYa.Tests;

public class OrderStateMachineTests
{
    [Fact] public void Pendiente_a_Confirmado_es_valido() => Assert.True(new Order().TryTransition(OrderStatus.Confirmado));

    [Fact] public void Rechazado_exige_motivo()
    {
        var o = new Order();
        Assert.False(o.TryTransition(OrderStatus.Rechazado));
        Assert.True(o.TryTransition(OrderStatus.Rechazado, RejectionReason.SinStock));
    }

    [Fact] public void Confirmado_solo_avanza_a_Entregado_o_Cancelado()
    {
        var o = new Order(); o.TryTransition(OrderStatus.Confirmado);
        Assert.False(o.TryTransition(OrderStatus.Pendiente));
        Assert.True(o.TryTransition(OrderStatus.Cancelado));
    }

    [Fact] public void Estados_finales_no_cambian()
    {
        var o = new Order(); o.TryTransition(OrderStatus.Rechazado, RejectionReason.PagoFallido);
        Assert.False(o.TryTransition(OrderStatus.Confirmado));
    }
}
