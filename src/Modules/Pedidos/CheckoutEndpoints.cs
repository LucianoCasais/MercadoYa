using System.Security.Claims;
using MercadoYa.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MercadoYa.Modules.Pedidos;

public static class CheckoutEndpoints
{
    /// POST /orders: solo rol cliente. El userId sale del claim `sub` del token, nunca del cuerpo.
    /// Header obligatorio Idempotency-Key. Modo Sync: 200 con el pedido ya en su estado final. Modo Async (dia 8+): 202 con el
    /// pedido Pendiente (el resultado se consulta con GET /orders/{id}); 503 si RabbitMQ no recibio el mensaje.
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapPost("/orders", async (CreateOrderRequest body, HttpContext http, CheckoutService checkout, CancellationToken ct) =>
        {
            var userId = http.User.FindFirst("sub")?.Value ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var key = http.Request.Headers[MercadoYa.Shared.Headers.IdempotencyKey].ToString();
            var correlationId = http.Request.Headers["X-Correlation-Id"].ToString() is { Length: > 0 } c ? c : http.TraceIdentifier;
            var result = await checkout.PlaceAsync(userId, key, body, ct, BuyerInfo.From(http.User), correlationId);
            if (result.BrokerDown)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Servicio no disponible", detail: "No pudimos enviar el pedido a procesar. Probá de nuevo en unos minutos.");
            if (!result.IsValid)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Pedido invalido", detail: result.Error);
            return result.Accepted
                ? Results.Accepted($"/api/v1/orders/{result.Order!.Id}", result.Order)
                : Results.Ok(result.Order);
        }).RequireAuthorization(Policies.Cliente);

        // GET /orders/{id}: seguimiento. Solo el dueño del pedido; si es de otro usuario responde 404 (no revela que existe).
        v1.MapGet("/orders/{id:guid}", async (Guid id, HttpContext http, CheckoutService checkout, CancellationToken ct) =>
        {
            var userId = http.User.FindFirst("sub")?.Value ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            return await checkout.GetAsync(userId, id, ct) is { } order
                ? Results.Ok(order)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Pedido no encontrado", detail: $"No existe el pedido {id}.");
        }).RequireAuthorization(Policies.Cliente);
        return v1;
    }
}
