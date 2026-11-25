using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace MercadoYa.Shared;

/// Id de correlacion de la operacion en curso: el mismo valor recorre web -> API -> mensaje RabbitMQ -> worker -> API (stock)
/// -> Payments (metadata gRPC), y aparece como propiedad "CorrelationId" en los logs de todos los servicios.
/// Vive en un AsyncLocal: lo ve todo el codigo que corre dentro de la misma operacion asincrona, sin pasarlo por parametro.
public static class CorrelationContext
{
    public const int MaxLength = 64;
    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current => _current.Value;

    /// Fija el id para el resto de la operacion (y lo agrega a los logs). Al hacer Dispose vuelve el valor anterior.
    public static IDisposable Begin(string correlationId)
    {
        var previous = _current.Value;
        _current.Value = correlationId;
        var logProperty = LogContext.PushProperty("CorrelationId", correlationId);
        return new Restore(previous, logProperty);
    }

    /// Acepta el id recibido solo si es razonable (corto y sin caracteres raros: va a parar a logs y headers). Si no, genera uno.
    public static string FromIncoming(string? incoming) =>
        IsValid(incoming) ? incoming! : NewId();

    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// El trace id de la Activity actual si existe (lo comparten las herramientas de tracing); si no, un GUID.
    public static string NewId() => Activity.Current?.TraceId.ToHexString() is { } trace && trace != "00000000000000000000000000000000"
        ? trace : Guid.NewGuid().ToString("N");

    private sealed class Restore(string? previous, IDisposable logProperty) : IDisposable
    {
        public void Dispose()
        {
            logProperty.Dispose();
            _current.Value = previous;
        }
    }
}

public static class CorrelationMiddleware
{
    /// Cada request HTTP (y cada llamada gRPC: la metadata viaja como headers HTTP/2) toma el X-Correlation-Id que llega o
    /// genera uno, lo deja disponible en CorrelationContext y en los logs, lo escribe en el request (para el codigo que lee el
    /// header directamente) y lo devuelve en la respuesta. Va antes que el resto del pipeline para que todo el request lo vea.
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var id = CorrelationContext.FromIncoming(http.Request.Headers[Headers.CorrelationId].ToString());
        http.Request.Headers[Headers.CorrelationId] = id;
        http.Response.OnStarting(() =>
        {
            http.Response.Headers[Headers.CorrelationId] = id;
            return Task.CompletedTask;
        });
        using (CorrelationContext.Begin(id))
            await next(http);
    });
}

/// Handler de los clientes salientes (HttpClient del stock y canal gRPC de pagos): agrega el X-Correlation-Id de la operacion
/// en curso. En gRPC un header HTTP/2 ES metadata, asi que el mismo handler sirve para los dos.
public sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (CorrelationContext.Current is { } id && !request.Headers.Contains(Headers.CorrelationId))
            request.Headers.TryAddWithoutValidation(Headers.CorrelationId, id);
        return base.SendAsync(request, ct);
    }
}
