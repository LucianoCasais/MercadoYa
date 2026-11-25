using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MercadoYa.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MercadoYa.Modules.Pedidos;

/// Cliente HTTP real del stock: llama a /internal/v1/stock/** de la API con el token de servicio de `orders-worker`.
/// Hoy lo usa el checkout sincrono (la API se llama a si misma); desde el dia 9 lo usa el worker. Misma interfaz que el fake.
/// - Reservar: 200 = reservado (tambien si ya estaba reservado), 409 = sin stock; cualquier otra respuesta lanza excepcion
///   (el CheckoutService la traduce a StockNoDisponible).
/// - Liberar: idempotente; si la respuesta no es exitosa lanza excepcion.
public sealed class HttpStockClient(HttpClient http, ServiceTokenProvider tokens) : IStockClient
{
    public async Task<StockReserveOutcome> ReserveAsync(Guid orderId, IReadOnlyList<OrderLineRequest> items, CancellationToken ct)
    {
        var payload = new { orderId, items = items.Select(i => new { productId = i.ProductId, quantity = i.Quantity }) };
        using var res = await SendAsync(HttpMethod.Post, "/internal/v1/stock/reservations", JsonContent.Create(payload), ct);
        if (res.StatusCode == HttpStatusCode.Conflict) return StockReserveOutcome.InsufficientStock;
        res.EnsureSuccessStatusCode();
        return StockReserveOutcome.Reserved;
    }

    public async Task ReleaseAsync(Guid orderId, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Post, $"/internal/v1/stock/reservations/{orderId}/release", null, ct);
        res.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct));
        return await http.SendAsync(req, ct);
    }
}

public static class StockHttpClientExtensions
{
    /// Reemplaza el FakeStockClient por el cliente HTTP real. Se llama DESPUES de AddPedidosModule (la ultima registracion gana).
    /// Config: Api:BaseUrl (por defecto http://localhost:5001; en Docker http://localhost:8080 dentro del contenedor de la api).
    /// resilient = true (worker): el cliente se envuelve en Polly (reintentos, circuit breaker y timeout; ver Resilience.cs).
    /// La API lo deja en false: en modo Sync se llama a si misma y no tiene sentido reintentar dentro de la request del usuario.
    public static IServiceCollection AddStockHttpClient(this IServiceCollection services, IConfiguration config, bool resilient = false)
    {
        services.AddServiceTokenProvider(config);
        services.AddTransient<CorrelationIdHandler>();   // X-Correlation-Id de la operacion en curso en cada llamada
        Action<HttpClient> configure = c =>
        {
            c.BaseAddress = new Uri(config["Api:BaseUrl"] ?? "http://localhost:5001");
            c.Timeout = TimeSpan.FromSeconds(5);   // deadline por intento
        };

        if (!resilient)
        {
            services.AddHttpClient<IStockClient, HttpStockClient>(configure).AddHttpMessageHandler<CorrelationIdHandler>();
            return services;
        }

        services.AddHttpClient<HttpStockClient>(configure).AddHttpMessageHandler<CorrelationIdHandler>();
        services.AddStockResilience();
        services.AddTransient<IStockClient>(sp => new ResilientStockClient(sp.GetRequiredService<HttpStockClient>(), sp.GetRequiredService<StockResilience>().Pipeline));
        return services;
    }
}
