using MercadoYa.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MercadoYa.Modules.Pedidos;

public static class AdminOrderEndpoints
{
    /// /api/v1/admin/orders/**: solo rol admin (sin token 401, sin rol 403).
    public static IEndpointRouteBuilder MapAdminOrderEndpoints(this IEndpointRouteBuilder v1)
    {
        var g = v1.MapGroup("/admin/orders").RequireAuthorization(Policies.Admin).WithTags("Admin - Pedidos");

        // Listado paginado, mas nuevos primero. ?status=Confirmado filtra por estado.
        g.MapGet("", async (AdminOrderService svc, string? status, CancellationToken ct, int page = 1, int pageSize = AdminOrderService.DefaultPageSize) =>
        {
            var (result, error) = await svc.ListAsync(status, page, pageSize, ct);
            return result is null
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [error!] })
                : Results.Ok(result);
        });

        g.MapGet("/{id:guid}", async (Guid id, AdminOrderService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } order ? Results.Ok(order) : NotFound(id));

        // Confirmado -> Entregado | Cancelado (cancelar libera el stock).
        g.MapPatch("/{id:guid}/status", async (Guid id, ChangeOrderStatusRequest body, AdminOrderService svc, CancellationToken ct) =>
        {
            var r = await svc.ChangeStatusAsync(id, body.Status, ct);
            return r.Outcome switch
            {
                ChangeStatusOutcome.Changed => Results.Ok(r.Order),
                ChangeStatusOutcome.NotFound => NotFound(id),
                ChangeStatusOutcome.InvalidStatus => Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [r.Detail!] }),
                ChangeStatusOutcome.InvalidTransition => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Transicion no permitida", detail: r.Detail),
                _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Stock no disponible", detail: r.Detail),
            };
        });
        return v1;
    }

    private static IResult NotFound(Guid id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Pedido no encontrado", detail: $"No existe el pedido {id}.");
}
