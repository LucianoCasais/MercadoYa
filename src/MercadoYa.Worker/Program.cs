using MercadoYa.Modules.Pedidos;
using MercadoYa.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Pedidos: EF Core + OrderSagaProcessor + ISagaOrderStore (con FAKES de stock y pago; las dos lineas siguientes los reemplazan).
builder.Services.AddPedidosModule(builder.Configuration.GetConnectionString("Pedidos")!);

// Stock real: HTTP a /internal/v1/stock/** de la API con el token de servicio de orders-worker (Keycloak client credentials).
builder.Services.AddStockHttpClient(builder.Configuration);

// TODO(Integrante A): cuando este GrpcPaymentGateway (cliente gRPC de payments.proto, Payments:GrpcUrl), registrarlo aca:
//   builder.Services.AddScoped<IPaymentGateway, GrpcPaymentGateway>();
// Mientras tanto se usa FakePaymentGateway (rechaza importes que terminan en 999 centavos, igual que el servicio Payments).

builder.Services.AddSingleton(RabbitMqOptions.From(builder.Configuration));
builder.Services.AddHostedService<OrderSagaWorker>();

builder.Build().Run();
