using System.Security.Claims;
using MercadoYa.Modules.Pedidos;
using Xunit;

namespace MercadoYa.Tests;

public class BuyerInfoTests
{
    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact] public void Usa_el_claim_name_si_existe() =>
        Assert.Equal("Cliente Prueba", BuyerInfo.From(User(("name", "Cliente Prueba"), ("preferred_username", "cliente@mercadoya.local"))));

    [Fact] public void Sin_name_une_nombre_y_apellido() =>
        Assert.Equal("Cliente Prueba", BuyerInfo.From(User(("given_name", "Cliente"), ("family_name", "Prueba"))));

    [Fact] public void Sin_nombre_usa_el_usuario() =>
        Assert.Equal("cliente@mercadoya.local", BuyerInfo.From(User(("preferred_username", "cliente@mercadoya.local"))));

    [Fact] public void Sin_usuario_usa_el_email() =>
        Assert.Equal("a@b.com", BuyerInfo.From(User(("email", "a@b.com"))));

    [Fact] public void Sin_datos_devuelve_vacio() =>
        Assert.Equal("", BuyerInfo.From(User()));

    [Fact]
    public void Clean_recorta_espacios_y_limita_a_150()
    {
        Assert.Equal("Ana", BuyerInfo.Clean("  Ana  "));
        Assert.Equal("", BuyerInfo.Clean(null));
        Assert.Equal(BuyerInfo.MaxLength, BuyerInfo.Clean(new string('a', 400)).Length);
    }
}
