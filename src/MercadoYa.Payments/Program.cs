using MercadoYa.Payments;
using MercadoYa.Shared;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Payments")));
builder.Services.AddKeycloakAuth(builder.Configuration);
builder.Services.AddGrpc();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGrpcService<PaymentsService>();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.Run();
