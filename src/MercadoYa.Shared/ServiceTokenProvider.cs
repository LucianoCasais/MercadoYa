using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MercadoYa.Shared;

/// Datos del client de servicio de Keycloak (client credentials). Los defaults son los de desarrollo local.
public sealed record ServiceTokenOptions(string TokenUrl, string ClientId, string ClientSecret);

/// Obtiene (y guarda en memoria hasta poco antes de que venza) el token de servicio del client `orders-worker`.
/// Lo usan los clientes HTTP hacia endpoints internos: el stock hoy desde la API y, desde el dia 9, el worker.
/// Es singleton: el cache de token vive en esta instancia.
public sealed class ServiceTokenProvider(IHttpClientFactory httpFactory, ServiceTokenOptions options, TimeProvider clock)
{
    public const string HttpClientName = "keycloak-token";
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);   // se renueva un poco antes de que venza

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (IsValid()) return _token!;
        await _gate.WaitAsync(ct);
        try
        {
            if (IsValid()) return _token!;   // otro hilo ya lo renovo mientras esperaba
            using var http = httpFactory.CreateClient(HttpClientName);
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
            });
            using var res = await http.PostAsync(options.TokenUrl, body, ct);
            res.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            _token = doc.RootElement.GetProperty("access_token").GetString()
                     ?? throw new InvalidOperationException("Keycloak no devolvio access_token.");
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 60;
            _expiresAt = clock.GetUtcNow().AddSeconds(seconds) - Margin;
            return _token;
        }
        finally { _gate.Release(); }
    }

    private bool IsValid() => _token is not null && clock.GetUtcNow() < _expiresAt;
}

public static class ServiceTokenExtensions
{
    /// Config: Keycloak:TokenUrl (por defecto {Authority}/protocol/openid-connect/token), Keycloak:ClientId (orders-worker)
    /// y Keycloak:ClientSecret (por defecto el de desarrollo del realm). En Docker se pisan con variables de entorno.
    public static IServiceCollection AddServiceTokenProvider(this IServiceCollection services, IConfiguration config)
    {
        var kc = config.GetSection("Keycloak");
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ServiceTokenOptions(
            kc["TokenUrl"] ?? $"{kc["Authority"]}/protocol/openid-connect/token",
            kc["ClientId"] ?? KeycloakClients.OrdersWorker,
            kc["ClientSecret"] ?? "dev-only-change-me"));
        services.AddHttpClient(ServiceTokenProvider.HttpClientName);
        services.TryAddSingleton<ServiceTokenProvider>();
        return services;
    }
}
