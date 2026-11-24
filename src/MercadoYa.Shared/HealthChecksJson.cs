using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MercadoYa.Shared;

/// Health checks iguales en la API y en Payments:
/// - /health/live: el proceso responde (no mira dependencias). Si falla, Docker puede reiniciar el contenedor.
/// - /health/ready: ademas llega a sus dependencias (los checks con tag "ready", por ejemplo su base). Si falla, no esta listo
///   para atender, pero NO conviene reiniciarlo: la base volvera sola.
/// La respuesta es JSON con el estado de cada check, asi se ve que dependencia falla (200 = Healthy, 503 = Unhealthy).
public static class HealthChecksJson
{
    public const string ReadyTag = "ready";

    public static IEndpointRouteBuilder MapLiveAndReady(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync });
        return app;
    }

    private static Task WriteAsync(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                error = e.Value.Exception?.Message,   // solo el mensaje: nada de cadenas de conexion ni stack traces
            }),
        }));
    }
}
