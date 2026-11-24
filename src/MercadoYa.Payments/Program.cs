using MercadoYa.Payments;
using MercadoYa.Shared;
using Microsoft.EntityFrameworkCore;

// Servicio de pagos: gRPC (HTTP/2) para el cobro + HTTP/1.1 para health checks, en puertos distintos (ver Kestrel en appsettings).
// Local: gRPC en 5003 y health en 5004. Docker: 8081 y 8080 dentro del contenedor (docker-compose pisa Kestrel__Endpoints__*).
var builder = WebApplication.CreateBuilder(args);
var cs = builder.Configuration.GetConnectionString("Payments")
         ?? throw new InvalidOperationException("Falta ConnectionStrings:Payments.");

builder.Services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(cs));
builder.Services.AddScoped<PaymentProcessor>();
builder.Services.AddKeycloakAuth(builder.Configuration);   // firma, issuer, audience; la politica Worker exige azp = orders-worker
builder.Services.AddGrpc();
builder.Services.AddHealthChecks().AddNpgSql(cs, name: "payments-db", tags: [HealthChecksJson.ReadyTag]);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<PaymentsService>();
app.MapLiveAndReady();   // /health/live (el proceso responde) y /health/ready (y llega a su base); JSON por check
app.Run();
