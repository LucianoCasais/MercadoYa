using MercadoYa.Api;
using MercadoYa.Modules.Catalogo;
using MercadoYa.Modules.Inventario;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;

var builder = WebApplication.CreateBuilder(args);
var cs = builder.Configuration.GetConnectionString("Mercadoya")!;

builder.Services.AddCatalogoModule(cs).AddInventarioModule(cs).AddPedidosModule(cs);
builder.Services.AddStockHttpClient(builder.Configuration);   // stock REAL (reemplaza al fake); va DESPUES de AddPedidosModule
builder.Services.AddOrderMessaging(builder.Configuration);    // RabbitMQ + Checkout:Mode (Sync por defecto; Async desde que corre el worker)
builder.Services.AddScoped<IProductLookup, CatalogProductLookup>();   // adaptador Pedidos -> Catalogo
builder.Services.AddKeycloakAuth(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer().AddSwaggerGen();
// CORS: solo los origenes de la web (Cors:Origins en appsettings; en Docker se pisa con Cors__Origins__0, __1...).
// Solo los metodos que usa la API; los headers propios (Authorization, Idempotency-Key) los cubre AllowAnyHeader.
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() is { Length: > 0 } configured
    ? configured : ["http://localhost:3000", "http://localhost:4200"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins)
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE").AllowAnyHeader()
    .WithExposedHeaders("Location")                    // el 201 del alta de productos informa la URL del recurso
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));   // el navegador reusa el preflight y no lo repite en cada PATCH/PUT
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
