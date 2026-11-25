using Asp.Versioning;
using MercadoYa.Api;
using MercadoYa.Modules.Catalogo;
using MercadoYa.Modules.Inventario;
using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMercadoYaLogging(builder.Configuration, "api");   // Serilog estructurado (ver LoggingSetup)
var cs = builder.Configuration.GetConnectionString("Mercadoya")!;

builder.Services.AddCatalogoModule(cs).AddInventarioModule(cs).AddPedidosModule(cs);
builder.Services.AddStockHttpClient(builder.Configuration);   // stock REAL (reemplaza al fake); va DESPUES de AddPedidosModule
builder.Services.AddOrderMessaging(builder.Configuration);    // RabbitMQ + Checkout:Mode (Sync por defecto; Async desde que corre el worker)
builder.Services.AddScoped<IProductLookup, CatalogProductLookup>();   // adaptador Pedidos -> Catalogo
builder.Services.AddKeycloakAuth(builder.Configuration);
builder.Services.AddProblemDetails();
// Versionado por segmento de URL (/api/v1/...). Cada respuesta informa las versiones en el header api-supported-versions.
// Una version que no existe (/api/v2/...) es una URL que no existe: 404 (con ProblemDetails, ver UseStatusCodePages).
// Cuando haga falta un cambio incompatible se agrega un grupo v2 y v1 sigue funcionando para los clientes viejos.
builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.ReportApiVersions = true;
    o.ApiVersionReader = new UrlSegmentApiVersionReader();
}).AddApiExplorer(o =>
{
    o.GroupNameFormat = "'v'V";            // documento de Swagger "v1"
    o.SubstituteApiVersionInUrl = true;    // Swagger muestra /api/v1/... y no /api/v{version}/...
});
builder.Services.AddEndpointsApiExplorer().AddSwaggerGen();
// CORS: solo los origenes de la web (Cors:Origins en appsettings; en Docker se pisa con Cors__Origins__0, __1...).
// Solo los metodos que usa la API; los headers propios (Authorization, Idempotency-Key) los cubre AllowAnyHeader.
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() is { Length: > 0 } configured
    ? configured : ["http://localhost:3000", "http://localhost:4200"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins)
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE").AllowAnyHeader()
    .WithExposedHeaders("Location", Headers.CorrelationId)   // Location: URL del alta; X-Correlation-Id: para reportar un error
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));   // el navegador reusa el preflight y no lo repite en cada PATCH/PUT
// Listo = llega a su base. RabbitMQ NO entra: sin el, el catalogo y el admin siguen funcionando (solo falla el checkout Async).
builder.Services.AddHealthChecks().AddNpgSql(cs, name: "mercadoya-db", tags: [HealthChecksJson.ReadyTag]);

var app = builder.Build();
app.UseCorrelationId();          // X-Correlation-Id de cada request: en los logs, en la respuesta y hacia el worker/Payments
app.UseMercadoYaRequestLogging(); // una linea de log por request, con su CorrelationId
app.UseExceptionHandler();
app.UseStatusCodePages();   // un error sin cuerpo (404 de ruta o de recurso, 405...) sale como ProblemDetails, igual que el resto
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI();

app.MapLiveAndReady();

var v1 = app.NewVersionedApi("MercadoYa").MapGroup("/api/v{version:apiVersion}").HasApiVersion(1.0);
v1.MapCatalogoEndpoints();
v1.MapCatalogoAdminEndpoints();     // ABM de productos (rol admin)
v1.MapInventarioAdminEndpoints();   // PATCH de stock (rol admin)
v1.MapPedidosEndpoints();
app.MapInventarioInternalEndpoints();

app.Run();
