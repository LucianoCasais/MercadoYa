using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace MercadoYa.Shared;

public static class KeycloakAuthExtensions
{
    /// Valida firma, issuer, audience y rol. Authority = URL interna (Docker); Issuer = URL publica (localhost:8081).
    public static IServiceCollection AddKeycloakAuth(this IServiceCollection services, IConfiguration config)
    {
        var kc = config.GetSection("Keycloak");
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.Authority = kc["Authority"];
            o.RequireHttpsMetadata = false;           // solo desarrollo local
            o.MapInboundClaims = false;               // conserva `sub` y `azp` tal cual
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = kc["Issuer"],
                ValidateAudience = true, ValidAudience = kc["Audience"],
                RoleClaimType = ClaimTypes.Role
            };
            o.Events = new JwtBearerEvents { OnTokenValidated = c => { MapRealmRoles(c.Principal); return Task.CompletedTask; } };
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Policies.Cliente, p => p.RequireRole(Roles.Cliente))
            .AddPolicy(Policies.Worker, p => p.RequireClaim("azp", KeycloakClients.OrdersWorker));
        return services;
    }

    // Keycloak entrega los roles en realm_access.roles; se copian a claims de rol de .NET.
    private static void MapRealmRoles(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity id) return;
        var raw = id.FindFirst("realm_access")?.Value;
        if (raw is null) return;
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("roles", out var roles))
            foreach (var r in roles.EnumerateArray()) id.AddClaim(new Claim(ClaimTypes.Role, r.GetString()!));
    }
}
