using System.Text.Json;
using MercadoYa.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace MercadoYa.Modules.Pedidos;

// ---- Mensaje OrderRequested (record en MercadoYa.Shared; contrato: contracts/order-requested.json). JSON en camelCase. ----
public static class OrderMessages
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] ToBytes(this OrderRequested message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);
    public static OrderRequested? Parse(ReadOnlySpan<byte> body) => JsonSerializer.Deserialize<OrderRequested>(body, Json);

    public static OrderRequested ToRequestedMessage(this Order o) =>
        new(o.Id, o.UserId, o.Items.OrderBy(i => i.ProductId).Select(i => new OrderItemMessage(i.ProductId, i.Quantity)).ToList(), o.TotalCents);
}

/// Puerto de publicacion del mensaje. Si no se pudo entregar al broker (con confirmacion) lanza excepcion:
/// el checkout lo traduce a Rechazado (BrokerNoDisponible) y 503.
public interface IOrderPublisher
{
    Task PublishAsync(OrderRequested message, string correlationId, CancellationToken ct);
}

/// Como se procesa un pedido nuevo: Sync = la API hace todo en la request (dias 3-7); Async = la API solo crea el pedido
/// Pendiente, publica OrderRequested y responde 202; la SAGA la ejecuta el worker (dia 9).
public enum CheckoutMode { Sync, Async }
public sealed record CheckoutOptions(CheckoutMode Mode = CheckoutMode.Sync);

/// Nombres de la topologia de RabbitMQ. Se declara en infra/rabbitmq/definitions.json (se carga al arrancar el broker):
/// exchange direct `orders` -> cola quorum `orders` (routing key order.requested), con DLX `orders.dlx` -> cola `orders.dead`.
public static class RabbitTopology
{
    public const string Exchange = "orders";
    public const string RoutingKey = "order.requested";
    public const string Queue = "orders";
    public const string DeadLetterExchange = "orders.dlx";
    public const string DeadQueue = "orders.dead";
}

public sealed record RabbitMqOptions(string Host, int Port, string User, string Password, string VirtualHost)
{
    public static RabbitMqOptions From(IConfiguration config)
    {
        var s = config.GetSection("RabbitMq");
        return new(s["Host"] ?? "localhost", int.TryParse(s["Port"], out var p) ? p : AmqpTcpEndpoint.UseDefaultPort,
            s["User"] ?? "mercadoya", s["Password"] ?? "mercadoya_dev", s["VirtualHost"] ?? "/");
    }
}

/// Publisher con publisher confirms: PublishAsync solo termina bien cuando el broker confirma que guardo el mensaje
/// (cola quorum, mensaje persistente). Un nack, un mensaje sin ruta (mandatory, PublishException) o un timeout lanzan excepcion.
/// Mantiene una conexion y un canal para toda la vida de la API; si fallan se vuelven a crear en el proximo envio.
public sealed class RabbitOrderPublisher(RabbitMqOptions options, ILogger<RabbitOrderPublisher> logger) : IOrderPublisher, IAsyncDisposable
{
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);   // un envio a la vez: simple y sobrado para el volumen de pedidos
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(OrderRequested message, string correlationId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConfirmTimeout);
        await _gate.WaitAsync(timeout.Token);
        try
        {
            var channel = await GetChannelAsync(timeout.Token);
            var props = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = message.OrderId.ToString(),
                CorrelationId = correlationId,
                Headers = new Dictionary<string, object?> { [MercadoYa.Shared.Headers.CorrelationId] = correlationId },
            };
            // mandatory: si el exchange no tiene una cola enlazada el broker devuelve el mensaje y se lanza PublishException.
            await channel.BasicPublishAsync(RabbitTopology.Exchange, RabbitTopology.RoutingKey, mandatory: true, props, message.ToBytes(), timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "No se pudo publicar OrderRequested del pedido {OrderId}", message.OrderId);
            await ResetAsync();   // el proximo envio reabre conexion y canal
            throw;                // el checkout lo traduce a Rechazado (BrokerNoDisponible) y 503
        }
        finally { _gate.Release(); }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await ResetAsync();
        var factory = new ConnectionFactory
        {
            HostName = options.Host, Port = options.Port, UserName = options.User, Password = options.Password, VirtualHost = options.VirtualHost,
            ClientProvidedName = "mercadoya-api", RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
        };
        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        return _channel;
    }

    private async Task ResetAsync()
    {
        var (channel, connection) = (_channel, _connection);
        (_channel, _connection) = (null, null);
        try { if (channel is not null) await channel.DisposeAsync(); } catch { /* ya estaba caido */ }
        try { if (connection is not null) await connection.DisposeAsync(); } catch { /* ya estaba caido */ }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }
}

public static class MessagingExtensions
{
    /// Registra el publisher (singleton: una sola conexion) y la opcion Checkout:Mode (Sync por defecto).
    /// Config: RabbitMq:Host/Port/User/Password/VirtualHost y Checkout:Mode = Sync | Async.
    public static IServiceCollection AddOrderMessaging(this IServiceCollection services, IConfiguration config)
    {
        var mode = Enum.TryParse<CheckoutMode>(config["Checkout:Mode"], ignoreCase: true, out var m) ? m : CheckoutMode.Sync;
        services.AddSingleton(new CheckoutOptions(mode));
        services.AddSingleton(RabbitMqOptions.From(config));
        services.AddSingleton<IOrderPublisher, RabbitOrderPublisher>();
        return services;
    }
}
