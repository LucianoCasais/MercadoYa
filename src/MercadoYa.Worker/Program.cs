using MercadoYa.Modules.Pedidos;
using MercadoYa.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPedidosModule(builder.Configuration.GetConnectionString("Pedidos")!);
builder.Services.AddHostedService<OrderSagaWorker>();
builder.Build().Run();
