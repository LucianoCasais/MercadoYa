using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace MercadoYa.Shared;

/// Logs estructurados con Serilog, iguales en api, worker y payments:
/// - Cada evento lleva Service (que servicio lo escribio) y, si hay, CorrelationId y los datos del scope (OrderId en el worker).
/// - Local: texto legible con el CorrelationId en cada linea. Docker (Logging:Json = true): una linea JSON por evento,
///   lista para filtrar por CorrelationId (por ejemplo: docker compose logs | grep <id>).
/// - Niveles: Information para el sistema; Warning para el ruido de ASP.NET Core, EF Core (SQL) y HttpClient.
///   Se pueden pisar con la seccion "Serilog" de appsettings (Serilog:MinimumLevel:Override:...).
public static class LoggingSetup
{
    public const string TextTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Service} {CorrelationId} {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static IServiceCollection AddMercadoYaLogging(this IServiceCollection services, IConfiguration config, string serviceName) =>
        services.AddSerilog((sp, log) =>
        {
            log.ReadFrom.Services(sp);   // sinks extra registrados en DI (los tests agregan uno para revisar los eventos)
            log.MinimumLevel.Information()
               .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
               .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
               .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
               .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
               .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
               .ReadFrom.Configuration(config)
               .Enrich.FromLogContext()
               .Enrich.WithProperty("Service", serviceName);
            if (config.GetValue<bool>("Logging:Json")) log.WriteTo.Console(new CompactJsonFormatter());
            else log.WriteTo.Console(outputTemplate: TextTemplate);
        }, preserveStaticLogger: true);   // cada host usa SU logger (no el global Log.Logger): en los tests corren varios a la vez

    /// Una linea por request HTTP/gRPC (metodo, ruta, status, duracion) con su CorrelationId. Los health checks van en Debug
    /// para no llenar el log con el healthcheck de Docker cada 10 s. Va despues de UseCorrelationId.
    public static IApplicationBuilder UseMercadoYaRequestLogging(this IApplicationBuilder app) =>
        app.UseSerilogRequestLogging(o =>
        {
            o.Logger = app.ApplicationServices.GetRequiredService<ILogger>();   // el logger del host, no el estatico
            o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} -> {StatusCode} en {Elapsed:0} ms";
            o.GetLevel = (http, _, ex) =>
                ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug
                : LogEventLevel.Information;
        });
}
