using MercadoYa.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MercadoYa.Api;

/// Documentacion OpenAPI (Swagger) de la API: /swagger (interfaz) y /swagger/v1/swagger.json (especificacion).
/// - Info del documento y esquema de seguridad Bearer (boton "Authorize" de Swagger UI: se pega el JWT de Keycloak).
/// - Cada endpoint declara su resumen, descripcion, tag y respuestas en su propio modulo (WithSummary, Produces, ProducesProblem...).
/// - Los filtros de abajo completan lo que sale de los metadatos de seguridad: el candado y los 401/403 solo en los endpoints
///   protegidos (los publicos no los llevan) y el header Idempotency-Key de POST /orders.
/// La especificacion se exporta a contracts/openapi-v1.json con tests/e2e/exportar_openapi.py.
public static class OpenApiSetup
{
    public const string BearerScheme = "Bearer";

    public static IServiceCollection AddMercadoYaSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        return services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "MercadoYa API",
                Version = "v1",
                Description =
                    "API REST de MercadoYa: catálogo público, checkout de clientes, administración de productos, stock y pedidos, y endpoints internos del worker.\n\n" +
                    "**Autenticación:** JWT de Keycloak (realm `mercadoya`). Para probar los endpoints protegidos, obtené un token, apretá **Authorize** y pegalo (sin la palabra Bearer).\n\n" +
                    "**Errores:** todos siguen ProblemDetails (RFC 9457). Cada respuesta lleva el header `X-Correlation-Id`, útil para reportar un problema; también se puede enviar uno propio en la request.\n\n" +
                    "**Compra asíncrona:** `POST /api/v1/orders` responde 202 con el pedido Pendiente; el resultado final se consulta con `GET /api/v1/orders/{id}`.",
            });
            o.AddSecurityDefinition(BearerScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "JWT emitido por Keycloak.",
            });
            o.OperationFilter<AuthorizationOperationFilter>();
            o.OperationFilter<IdempotencyKeyOperationFilter>();
        });
    }
}

/// Marca con el candado (y con 401 y 403) solo los endpoints que exigen autorizacion, y dice en su descripcion quien puede usarlos.
public sealed class AuthorizationOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any()) return;
        if (!metadata.OfType<IAuthorizeData>().Any()) return;   // sin RequireAuthorization: endpoint publico
        var policies = metadata.OfType<IAuthorizeData>().Select(a => a.Policy).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();

        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = OpenApiSetup.BearerScheme } }] = Array.Empty<string>(),
        });
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Falta el token o no es válido (vencido, firma, emisor o audiencia incorrectos)." });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "El token es válido pero su rol no tiene permiso para esta operación." });

        var who = string.Join(", ", policies.Select(Describe));
        if (who.Length > 0)
            operation.Description = string.IsNullOrEmpty(operation.Description) ? $"**Acceso:** {who}." : $"{operation.Description}\n\n**Acceso:** {who}.";
    }

    private static string Describe(string? policy) => policy switch
    {
        Policies.Admin => "solo el rol `admin`",
        Policies.Cliente => "solo el rol `cliente`",
        Policies.Worker => "solo el client de servicio `orders-worker` (uso interno)",
        _ => $"política `{policy}`",
    };
}

/// Documenta el header Idempotency-Key como obligatorio en los endpoints marcados con RequiresIdempotencyKey.
public sealed class IdempotencyKeyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<RequiresIdempotencyKey>().Any()) return;
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = Headers.IdempotencyKey,
            In = ParameterLocation.Header,
            Required = true,
            Description = "Clave que identifica esta compra (hasta 100 caracteres, por ejemplo un GUID). Repetir la request con la misma clave devuelve el mismo pedido y no lo duplica.",
            Schema = new OpenApiSchema { Type = "string", MaxLength = 100 },
        });
    }
}
