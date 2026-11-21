using System.Net;
using System.Security.Cryptography;
using Grpc.Core;
using Grpc.Net.Client;
using MercadoYa.Payments;
using MercadoYa.Payments.Grpc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using PaymentsGrpc = MercadoYa.Payments.Grpc.Payments;

namespace MercadoYa.Tests;

/// Servicio de pagos completo (Program real: auth, politica Worker, gRPC, health) en memoria, contra la BD payments real.
/// Keycloak se reemplaza por una clave de firma del test: se prueba la VALIDACION del token (firma, issuer, audience,
/// vencimiento, azp), que es lo que hace el servicio. La emision real de tokens por Keycloak se probo a mano (README).
public sealed class PaymentsHost : WebApplicationFactory<PaymentsService>
{
    public const string Issuer = "http://localhost:8081/realms/mercadoya";   // el mismo que appsettings de Payments
    public const string Audience = "mercadoya-api";
    private readonly SymmetricSecurityKey _key = new(RandomNumberGenerator.GetBytes(32));
    private readonly string _connectionString;

    public PaymentsHost(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Payments", _connectionString);
        builder.ConfigureTestServices(s => s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            // Sin Keycloak: las claves de firma salen del test en vez de pedirse a /.well-known.
            o.Authority = null;
            o.ConfigurationManager = null;
            o.TokenValidationParameters.IssuerSigningKey = _key;
        }));
    }

    /// Token como los de Keycloak. azp = client que lo pidio (orders-worker = el worker por client credentials).
    public string Token(string azp, string audience = Audience, string issuer = Issuer, int expiresInMinutes = 5) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = DateTime.UtcNow.AddMinutes(Math.Min(0, expiresInMinutes) - 30),
            IssuedAt = DateTime.UtcNow.AddMinutes(Math.Min(0, expiresInMinutes) - 30),
            Expires = DateTime.UtcNow.AddMinutes(expiresInMinutes),
            Claims = new Dictionary<string, object> { ["azp"] = azp, ["sub"] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });

    public PaymentsGrpc.PaymentsClient Client() =>
        new(GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() }));
}

[Collection("payments-db")]
[Trait("Category", "Integracion")]
public sealed class PaymentsGrpcTests(PaymentsDbFixture pg) : IDisposable
{
    private readonly PaymentsHost _host = new(pg.ConnectionString);

    public void Dispose() => _host.Dispose();

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private Task<ChargeOrderReply> Charge(string? token, string orderId, long cents) =>
        _host.Client().ChargeOrderAsync(new ChargeOrderRequest { OrderId = orderId, AmountCents = cents },
            token is null ? new Metadata() : Bearer(token)).ResponseAsync;

    private async Task<StatusCode> FailsWith(string? token, string orderId, long cents) =>
        (await Assert.ThrowsAsync<RpcException>(() => Charge(token, orderId, cents))).StatusCode;

    // ---- client credentials: solo orders-worker puede cobrar ----

    [Fact]
    public async Task Sin_token_responde_Unauthenticated() =>
        Assert.Equal(StatusCode.Unauthenticated, await FailsWith(null, Guid.NewGuid().ToString(), 1000));

    [Fact]
    public async Task Token_de_otro_client_responde_PermissionDenied() =>   // un usuario logueado en la web (client `web`), aunque sea admin
        Assert.Equal(StatusCode.PermissionDenied, await FailsWith(_host.Token(azp: "web"), Guid.NewGuid().ToString(), 1000));

    [Fact]
    public async Task Token_vencido_responde_Unauthenticated() =>
        Assert.Equal(StatusCode.Unauthenticated, await FailsWith(_host.Token("orders-worker", expiresInMinutes: -10), Guid.NewGuid().ToString(), 1000));

    [Fact]
    public async Task Token_para_otra_audiencia_responde_Unauthenticated() =>
        Assert.Equal(StatusCode.Unauthenticated, await FailsWith(_host.Token("orders-worker", audience: "otra-api"), Guid.NewGuid().ToString(), 1000));

    [Fact]
    public async Task Token_de_otro_emisor_responde_Unauthenticated() =>
        Assert.Equal(StatusCode.Unauthenticated, await FailsWith(_host.Token("orders-worker", issuer: "http://evil.example/realms/x"), Guid.NewGuid().ToString(), 1000));

    [Fact]
    public async Task Token_firmado_con_otra_clave_responde_Unauthenticated()
    {
        using var otro = new PaymentsHost(pg.ConnectionString);   // otra instancia = otra clave de firma
        Assert.Equal(StatusCode.Unauthenticated, await FailsWith(otro.Token("orders-worker"), Guid.NewGuid().ToString(), 1000));
    }

    // ---- cobro por gRPC ----

    [Fact]
    public async Task Worker_cobra_y_la_repeticion_devuelve_lo_mismo()
    {
        var token = _host.Token("orders-worker");
        var order = Guid.NewGuid();

        var first = await Charge(token, order.ToString(), 480000);
        var again = await Charge(token, order.ToString(), 480000);

        Assert.Equal(PaymentStatus.Aprobado, first.Status);
        Assert.Equal(first, again);
        Assert.Single(await pg.RecordsOfAsync(order));
    }

    [Fact]
    public async Task Importe_terminado_en_999_responde_Rechazado_con_motivo()
    {
        var r = await Charge(_host.Token("orders-worker"), Guid.NewGuid().ToString(), 999);

        Assert.Equal((PaymentStatus.Rechazado, PaymentRules.RejectedReason), (r.Status, r.Reason));
    }

    [Theory]
    [InlineData("no-es-un-guid", 1000)]
    [InlineData("00000000-0000-0000-0000-000000000000", 1000)]
    [InlineData("3f2a9c1e-5b7d-4e8a-9c21-7d4e5f6a8b90", 0)]
    [InlineData("3f2a9c1e-5b7d-4e8a-9c21-7d4e5f6a8b90", -1)]
    public async Task Datos_invalidos_responden_InvalidArgument(string orderId, long cents) =>
        Assert.Equal(StatusCode.InvalidArgument, await FailsWith(_host.Token("orders-worker"), orderId, cents));

    [Fact]
    public async Task Mismo_pedido_con_otro_importe_responde_FailedPrecondition()
    {
        var token = _host.Token("orders-worker");
        var order = Guid.NewGuid().ToString();
        await Charge(token, order, 480000);

        Assert.Equal(StatusCode.FailedPrecondition, await FailsWith(token, order, 1000));
    }

    [Fact]
    public async Task Base_de_pagos_caida_responde_Unavailable_y_ready_503()
    {
        // Un puerto sin nada escuchando: la conexion a la base falla como si PostgreSQL estuviera caido.
        using var caido = new PaymentsHost("Host=127.0.0.1;Port=1;Database=payments;Username=x;Password=x;Timeout=2");

        var status = (await Assert.ThrowsAsync<RpcException>(() => caido.Client()
            .ChargeOrderAsync(new ChargeOrderRequest { OrderId = Guid.NewGuid().ToString(), AmountCents = 1000 },
                Bearer(caido.Token("orders-worker"))).ResponseAsync)).StatusCode;

        Assert.Equal(StatusCode.Unavailable, status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await caido.CreateClient().GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await caido.CreateClient().GetAsync("/health/live")).StatusCode);
    }

    // ---- health checks (sin token) ----

    [Fact]
    public async Task Health_checks_responden_sin_token()
    {
        var http = _host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/ready")).StatusCode);
    }
}
