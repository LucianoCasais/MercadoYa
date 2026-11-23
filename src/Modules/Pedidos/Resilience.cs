using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace MercadoYa.Modules.Pedidos;

/// Parametros de resiliencia de las llamadas del worker (stock y pagos). Valores de la guia (seccion 4.7):
/// timeout de 5 s por intento, 3 reintentos con backoff exponencial y jitter, y circuit breaker que se abre cuando
/// fallan al menos la mitad de las llamadas de los ultimos 30 s (con un minimo de 5 llamadas) y espera 30 s.
public sealed record ResilienceOptions(
    TimeSpan AttemptTimeout,
    int MaxRetries,
    TimeSpan RetryDelay,
    double BreakerFailureRatio,
    int BreakerMinimumThroughput,
    TimeSpan BreakerSamplingDuration,
    TimeSpan BreakDuration)
{
    public static ResilienceOptions Default { get; } = new(
        AttemptTimeout: TimeSpan.FromSeconds(5),
        MaxRetries: 3,
        RetryDelay: TimeSpan.FromMilliseconds(500),   // con backoff exponencial: ~0,5 s, ~1 s, ~2 s (mas jitter)
        BreakerFailureRatio: 0.5,
        BreakerMinimumThroughput: 5,
        BreakerSamplingDuration: TimeSpan.FromSeconds(30),
        BreakDuration: TimeSpan.FromSeconds(30));
}

/// El servicio de pagos respondio "no disponible" (como valor, no como excepcion): se trata como fallo transitorio.
public sealed class TransientPaymentFault(string message) : Exception(message);

public static class TransientFaults
{
    /// Que errores vale la pena reintentar: caidas de red, timeouts, 5xx, 408 y 429. NO se reintentan los 4xx (400, 401, 404...),
    /// que son errores propios de la llamada y fallarian igual, ni las excepciones de logica.
    /// Seguro reintentar porque las tres operaciones son idempotentes por pedido (reservar, liberar y cobrar).
    public static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutRejectedException => true,
        TransientPaymentFault => true,
        HttpRequestException h => h.StatusCode is null || (int)h.StatusCode >= 500
            || h.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        TimeoutException => true,
        TaskCanceledException => true,   // timeout del HttpClient; si cancelo el llamador, Polly no reintenta
        _ => false,
    };
}

public static class ResiliencePipelines
{
    /// Pipeline Polly: reintentos (afuera) -> circuit breaker -> timeout por intento (adentro).
    /// El timeout va adentro del breaker para que un intento colgado cuente como fallo, y el breaker adentro del retry para que
    /// cada intento se mida por separado. Un circuito abierto NO se reintenta: falla enseguida (BrokenCircuitException).
    /// Debe crearse UNA vez por dependencia y compartirse (el estado del breaker vive en el pipeline).
    public static ResiliencePipeline Create(string name, ResilienceOptions options, ILogger? logger = null, TimeProvider? time = null)
    {
        static ValueTask<bool> Transient<T>(Outcome<T> outcome) => ValueTask.FromResult(outcome.Exception is { } e && TransientFaults.IsTransient(e));

        var builder = new ResiliencePipelineBuilder { TimeProvider = time ?? TimeProvider.System };

        if (options.MaxRetries > 0)   // Polly exige al menos 1 reintento; con 0 simplemente no se agrega la estrategia
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = args => Transient(args.Outcome),
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryDelay,
                OnRetry = args =>
                {
                    logger?.LogWarning("[{Pipeline}] intento {Attempt} fallido ({Error}); se reintenta en {Delay} ms",
                        name, args.AttemptNumber + 1, args.Outcome.Exception?.GetType().Name, (int)args.RetryDelay.TotalMilliseconds);
                    return default;
                },
            });

        return builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = args => Transient(args.Outcome),
                FailureRatio = options.BreakerFailureRatio,
                MinimumThroughput = options.BreakerMinimumThroughput,
                SamplingDuration = options.BreakerSamplingDuration,
                BreakDuration = options.BreakDuration,
                OnOpened = args =>
                {
                    logger?.LogError("[{Pipeline}] circuit breaker ABIERTO por {Seconds} s: las llamadas fallan sin intentarse", name, args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    logger?.LogWarning("[{Pipeline}] circuit breaker semiabierto: se prueba una llamada", name);
                    return default;
                },
                OnClosed = _ =>
                {
                    logger?.LogInformation("[{Pipeline}] circuit breaker CERRADO: el servicio se recupero", name);
                    return default;
                },
            })
            .AddTimeout(new TimeoutStrategyOptions { Timeout = options.AttemptTimeout })
            .Build();
    }
}

/// Decorador del puerto de stock con el pipeline de resiliencia. Los resultados de negocio (reservado, sin stock) pasan tal cual;
/// solo las excepciones transitorias se reintentan. Si se agotan los reintentos o el circuito esta abierto, la excepcion llega
/// al procesador de la SAGA, que la trata como "stock no disponible".
public sealed class ResilientStockClient(IStockClient inner, ResiliencePipeline pipeline) : IStockClient
{
    public async Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct) =>
        await pipeline.ExecuteAsync(async token => await inner.ReserveAsync(orderId, items, token), ct);

    public async Task ReleaseAsync(Guid orderId, CancellationToken ct) =>
        await pipeline.ExecuteAsync(async token => await inner.ReleaseAsync(orderId, token), ct);
}

/// Decorador del puerto de pagos. "Rechazado" es una respuesta valida y NO se reintenta. "No disponible" (devuelto o lanzado),
/// timeouts y caidas se reintentan; si se agotan los reintentos o el circuito esta abierto devuelve PaymentOutcome.Unavailable
/// (el worker compensa: libera stock y rechaza con PagoNoDisponible).
public sealed class ResilientPaymentGateway(IPaymentGateway inner, ResiliencePipeline pipeline, ILogger? logger = null) : IPaymentGateway
{
    public async Task<PaymentResult> ChargeAsync(Guid orderId, long amountCents, CancellationToken ct)
    {
        try
        {
            return await pipeline.ExecuteAsync(async token =>
            {
                var result = await inner.ChargeAsync(orderId, amountCents, token);
                if (result.Outcome == PaymentOutcome.Unavailable) throw new TransientPaymentFault(result.Reason ?? "Servicio de pagos no disponible");
                return result;
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "Cobro del pedido {OrderId} no disponible tras los reintentos ({Error})", orderId, ex.GetType().Name);
            return new PaymentResult(PaymentOutcome.Unavailable, "Servicio de pagos no disponible");
        }
    }
}

/// Un pipeline compartido (singleton) por dependencia: el circuit breaker tiene que ver las llamadas de todos los mensajes.
public sealed record StockResilience(ResiliencePipeline Pipeline);
public sealed record PaymentResilience(ResiliencePipeline Pipeline);

public static class ResilienceExtensions
{
    internal static IServiceCollection AddStockResilience(this IServiceCollection services) =>
        services.AddSingleton(sp => new StockResilience(ResiliencePipelines.Create("stock", ResilienceOptions.Default,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("MercadoYa.Resilience.Stock"))));

    /// Registra TInner (por ejemplo GrpcPaymentGateway) como IPaymentGateway protegido con Polly. Se llama DESPUES de AddPedidosModule.
    public static IServiceCollection AddResilientPaymentGateway<TInner>(this IServiceCollection services) where TInner : class, IPaymentGateway
    {
        services.AddSingleton(sp => new PaymentResilience(ResiliencePipelines.Create("payments", ResilienceOptions.Default,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("MercadoYa.Resilience.Payments"))));
        services.AddScoped<TInner>();
        services.AddScoped<IPaymentGateway>(sp => new ResilientPaymentGateway(sp.GetRequiredService<TInner>(),
            sp.GetRequiredService<PaymentResilience>().Pipeline, sp.GetRequiredService<ILoggerFactory>().CreateLogger("MercadoYa.Resilience.Payments")));
        return services;
    }
}
