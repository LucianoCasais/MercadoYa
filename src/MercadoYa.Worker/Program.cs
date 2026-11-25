using MercadoYa.Modules.Pedidos;
using MercadoYa.Shared;
using MercadoYa.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMercadoYaLogging(builder.Configuration, "worker");

// Pedidos: EF Core + OrderSagaProcessor + ISagaOrderStore (con FAKES de stock y pago; las dos lineas siguientes los reemplazan).
builder.Services.AddPedidosModule(builder.Configuration.GetConnectionString("Pedidos")!);

// Stock real: HTTP a /internal/v1/stock/** de la API con el token de servicio de orders-worker (Keycloak client credentials),
// protegido con Polly (timeout de 5 s, 3 reintentos con backoff y jitter, circuit breaker; ver Resilience.cs).
builder.Services.AddStockHttpClient(builder.Configuration, resilient: true);

// Pagos REAL: gRPC a Payments.ChargeOrder (Payments:GrpcUrl) con el mismo token de servicio de orders-worker,
// protegido con el mismo pipeline Polly (ver GrpcPaymentGateway.cs y Resilience.cs).
builder.Services.AddGrpcPaymentGateway(builder.Configuration);

builder.Services.AddSingleton(RabbitMqOptions.From(builder.Configuration));
builder.Services.AddHostedService<OrderSagaWorker>();

builder.Build().Run();
