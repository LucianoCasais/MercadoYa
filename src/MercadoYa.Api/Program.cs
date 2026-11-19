using MercadoYa.Api;
using MercadoYa.Modules.Catalogo;
using MercadoYa.Modules.Inventario;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;

var builder = WebApplication.CreateBuilder(args);
var cs = builder.Configuration.GetConnectionString("Mercadoya")!;

builder.Services.AddCatalogoModule(cs).AddInventarioModule(cs).AddPedidosModule(cs);
builder.Services.AddScoped<IProductLookup, CatalogProductLookup>();   // adaptador Pedidos -> Catalogo
builder.Services.AddKeycloakAuth(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer().AddSwaggerGen();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:3000", "http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks().AddNpgSql(cs, tags: ["ready"]);

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });

var v1 = app.MapGroup("/api/v1");
v1.MapCatalogoEndpoints();
v1.MapCatalogoAdminEndpoints();     // ABM de productos (rol admin)
v1.MapInventarioAdminEndpoints();   // PATCH de stock (rol admin)
v1.MapPedidosEndpoints();
app.MapInventarioInternalEndpoints();

app.Run();
