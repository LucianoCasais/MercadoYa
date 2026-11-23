using MercadoYa.Modules.Pedidos;
using MercadoYa.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Pedidos: EF Core + OrderSagaProcessor + ISagaOrderStore (con FAKES de stock y pago; las dos lineas siguientes los reemplazan).
builder.Services.AddPedidosModule(builder.Configuration.GetConnectionString("Pedidos")!);

// Stock real: HTTP a /internal/v1/stock/** de la API con el token de servicio de orders-worker (Keycloak client credentials),
// protegido con Polly (timeout de 5 s, 3 reintentos con backoff y jitter, circuit breaker; ver Resilience.cs).
builder.Services.AddStockHttpClient(builder.Configuration, resilient: true);

// Pagos, tambien con Polly. TODO(Integrante A): cuando este GrpcPaymentGateway (cliente gRPC de payments.proto, Payments:GrpcUrl),
// cambiar FakePaymentGateway por GrpcPaymentGateway en la linea siguiente. Mientras tanto se usa el fake (rechaza los importes
// que terminan en 999 centavos, igual que el servicio Payments).
builder.Services.AddResilientPaymentGateway<FakePaymentGateway>();

builder.Services.AddSingleton(RabbitMqOptions.From(builder.Configuration));
builder.Services.AddHostedService<OrderSagaWorker>();

builder.Build().Run();
