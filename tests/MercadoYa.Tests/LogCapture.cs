using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace MercadoYa.Tests;

/// Sink de Serilog que guarda los eventos en memoria. Los hosts de prueba lo registran en DI y LoggingSetup lo toma con
/// ReadFrom.Services: asi los tests ven exactamente lo que el servicio escribiria en sus logs.
public sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public IReadOnlyList<LogEvent> Events => _events.ToArray();

    /// Eventos que llevan la propiedad CorrelationId con ese valor.
    public IReadOnlyList<LogEvent> WithCorrelation(string correlationId) =>
        Events.Where(e => e.Properties.TryGetValue("CorrelationId", out var v) && v is ScalarValue { Value: string s } && s == correlationId).ToList();

    public static string Text(LogEvent e) => e.RenderMessage();
}
