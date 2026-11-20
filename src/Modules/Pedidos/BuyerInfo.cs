using System.Security.Claims;

namespace MercadoYa.Modules.Pedidos;

/// Nombre de quien compra, tomado de los claims del token de Keycloak (con MapInboundClaims = false los nombres son los originales).
/// Funcion pura: se prueba con xUnit sin necesidad de un token real.
public static class BuyerInfo
{
    public const int MaxLength = 150;

    /// Prioridad: `name`, luego `given_name` + `family_name`, luego `preferred_username` y por ultimo `email`.
    public static string From(ClaimsPrincipal user)
    {
        var name = user.FindFirst("name")?.Value;
        if (string.IsNullOrWhiteSpace(name))
            name = $"{user.FindFirst("given_name")?.Value} {user.FindFirst("family_name")?.Value}";
        if (string.IsNullOrWhiteSpace(name)) name = user.FindFirst("preferred_username")?.Value;
        if (string.IsNullOrWhiteSpace(name)) name = user.FindFirst("email")?.Value;
        return Clean(name);
    }

    /// Recorta espacios y limita al maximo de la columna (150).
    public static string Clean(string? name)
    {
        var t = name?.Trim() ?? "";
        return t.Length > MaxLength ? t[..MaxLength] : t;
    }
}
