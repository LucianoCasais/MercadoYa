using System.Security.Claims;
using MercadoYa.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MercadoYa.Modules.Pedidos;

public static class CheckoutEndpoints
{
    /// POST /orders: solo rol cliente. El userId sale del claim `sub` del token, nunca del cuerpo.
    /// Header obligatorio Idempotency-Key. Dia 3: responde 200 con el pedido ya en su estado final (flujo sincrono);
    /// el dia 8 pasa a 202 con el pedido Pendiente.
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapPost("/orders", async (CreateOrderRequest body, HttpContext http, CheckoutService checkout, CancellationToken ct) =>
        {
            var userId = http.User.FindFirst("sub")?.Value ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var key = http.Request.Headers[MercadoYa.Shared.Headers.IdempotencyKey].ToString();
            var result = await checkout.PlaceAsync(userId, key, body, ct);
            return result.IsValid
                ? Results.Ok(result.Order)
                : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Pedido invalido", detail: result.Error);
        }).RequireAuthorization(Policies.Cliente);
        return v1;
    }
}
