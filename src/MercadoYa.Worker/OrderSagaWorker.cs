using System.Text;
using MercadoYa.Modules.Pedidos;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MercadoYa.Worker;

/// Consumidor RabbitMQ de la SAGA del checkout (ver OrderSagaProcessor para la logica y docs/secuencia-checkout-saga.md).
///  - Cola `orders` (quorum, creada por infra/rabbitmq/definitions.json), prefetch 1 y ack manual: un mensaje a la vez por replica.
///    Se pueden levantar varias replicas del worker (docker compose up --scale worker=2): RabbitMQ reparte los mensajes y el
///    UPDATE condicional del pedido evita que dos replicas lo finalicen dos veces.
///  - Ack solo despues de dejar el pedido en su estado final. Si el worker muere a mitad de camino, RabbitMQ reentrega.
///  - Requeue (fallo transitorio) -> basic.reject con requeue; tras x-delivery-limit (5) el mensaje pasa al DLX y queda en `orders.dead`.
///    Se usa reject y NO nack: desde RabbitMQ 4.3 un basic.nack con requeue no suma al limite de entregas (el mensaje se reintentaria
///    para siempre), mientras que basic.reject si cuenta como intento fallido.
///  - Si RabbitMQ no esta disponible al arrancar (o se cae) el worker reintenta conectarse cada 5 s; la libreria recupera sola
///    la conexion y el consumidor ante cortes cortos.
public sealed class OrderSagaWorker(IServiceScopeFactory scopes, RabbitMqOptions options, ILogger<OrderSagaWorker> logger) : BackgroundService
{
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    /// Pausa antes de devolver un mensaje a la cola. Con x-delivery-limit 5 son ~50 s de margen, mas que los 30 s que el circuit breaker
    /// de stock queda abierto: asi una caida corta del stock no manda pedidos a orders.dead.
    public static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo consumir la cola {Queue} en {Host}:{Port}; se reintenta en {Seconds} s",
                    RabbitTopology.Queue, options.Host, options.Port, ReconnectDelay.TotalSeconds);
                try { await Task.Delay(ReconnectDelay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        logger.LogInformation("OrderSagaWorker detenido");
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = options.Host,
            Port = options.Port,
            UserName = options.User,
            Password = options.Password,
            VirtualHost = options.VirtualHost,
            ClientProvidedName = $"mercadoya-worker-{Environment.MachineName}",   // se ve en RabbitMQ Management (una conexion por replica)
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = ReconnectDelay,
        };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnMessageAsync(channel, delivery, ct);
        await channel.BasicConsumeAsync(RabbitTopology.Queue, autoAck: false, consumer, ct);
        logger.LogInformation("OrderSagaWorker consumiendo la cola {Queue} (prefetch 1, ack manual)", RabbitTopology.Queue);

        await Task.Delay(Timeout.Infinite, ct);   // la conexion y el consumidor se recuperan solos; se sale al detener el host
    }

    private async Task OnMessageAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var correlationId = ReadCorrelationId(delivery.BasicProperties);
        using var logScope = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId, ["OrderId"] = delivery.BasicProperties.MessageId });

        MessageDisposition disposition;
        try
        {
            using var scope = scopes.CreateScope();   // DbContext e HttpClient por mensaje
            var processor = scope.ServiceProvider.GetRequiredService<OrderSagaProcessor>();
            disposition = await processor.HandleAsync(delivery.Body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;   // apagado en curso: sin ack, RabbitMQ lo reentrega al volver a arrancar
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error inesperado procesando el mensaje; se devuelve a la cola");
            disposition = MessageDisposition.Requeue;
        }

        try
        {
            switch (disposition)
            {
                case MessageDisposition.Ack:
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
                    break;
                case MessageDisposition.DeadLetter:
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, ct);   // -> DLX orders.dlx -> orders.dead
                    break;
                default:
                    await Task.Delay(RequeueDelay, ct);
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: true, ct);   // cuenta como entrega fallida (x-delivery-limit)
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Canal cerrado justo ahora: el mensaje sin ack vuelve a la cola y la SAGA es idempotente, no se pierde nada.
            logger.LogWarning(ex, "No se pudo confirmar ({Disposition}) el mensaje; RabbitMQ lo reentregara", disposition);
        }
    }

    /// El publisher manda el id de correlacion en el header X-Correlation-Id (llega como bytes) y tambien en CorrelationId.
    private static string? ReadCorrelationId(IReadOnlyBasicProperties props)
    {
        if (props.Headers is not null && props.Headers.TryGetValue(MercadoYa.Shared.Headers.CorrelationId, out var raw))
            return raw switch { byte[] b => Encoding.UTF8.GetString(b), string s => s, _ => raw?.ToString() };
        return props.CorrelationId;
    }
}
