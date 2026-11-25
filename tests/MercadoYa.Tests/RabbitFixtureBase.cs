using System.Text.Json;
using MercadoYa.Modules.Pedidos;
using RabbitMQ.Client;
using Xunit;

namespace MercadoYa.Tests;

/// Logica comun del RabbitMQ de los tests de integracion: declara la topologia REAL (infra/rabbitmq/definitions.json, la misma que
/// carga docker compose), y ofrece lo necesario para vaciar las colas, contar mensajes y publicar mensajes crudos.
/// La subclase RabbitFixture levanta el broker en un contenedor (Testcontainers).
public abstract class RabbitFixtureBase : IAsyncLifetime
{
    public abstract RabbitMqOptions Options { get; }
    public abstract Task InitializeAsync();
    public virtual Task DisposeAsync() => Task.CompletedTask;

    private async Task<IConnection> ConnectAsync()
    {
        var o = Options;
        var factory = new ConnectionFactory { HostName = o.Host, Port = o.Port, UserName = o.User, Password = o.Password, VirtualHost = o.VirtualHost };
        return await factory.CreateConnectionAsync();
    }

    /// Crea exchanges, colas y bindings leyendo definitions.json (argumentos incluidos: quorum, x-delivery-limit, DLX).
    protected async Task DeclareTopologyAsync()
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(RepoFile("infra", "rabbitmq", "definitions.json")));
        var root = doc.RootElement;
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        foreach (var x in root.GetProperty("exchanges").EnumerateArray())
            await channel.ExchangeDeclareAsync(x.GetProperty("name").GetString()!, x.GetProperty("type").GetString()!,
                x.GetProperty("durable").GetBoolean(), x.GetProperty("auto_delete").GetBoolean(), Arguments(x));
        foreach (var q in root.GetProperty("queues").EnumerateArray())
            await channel.QueueDeclareAsync(q.GetProperty("name").GetString()!, q.GetProperty("durable").GetBoolean(), exclusive: false,
                q.GetProperty("auto_delete").GetBoolean(), Arguments(q));
        foreach (var b in root.GetProperty("bindings").EnumerateArray())
            await channel.QueueBindAsync(b.GetProperty("destination").GetString()!, b.GetProperty("source").GetString()!,
                b.GetProperty("routing_key").GetString()!, Arguments(b));
    }

    private static Dictionary<string, object?> Arguments(JsonElement owner)
    {
        var args = new Dictionary<string, object?>();
        if (!owner.TryGetProperty("arguments", out var a)) return args;
        foreach (var p in a.EnumerateObject())
            args[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : p.Value.GetString();
        return args;
    }

    /// Deja `orders` y `orders.dead` vacias: cada test arranca sin mensajes de otro.
    public async Task ResetAsync()
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueuePurgeAsync(RabbitTopology.Queue);
        await channel.QueuePurgeAsync(RabbitTopology.DeadQueue);
    }

    /// Mensajes en la cola (listos + sin confirmar no se distinguen: con MessageCount solo se ven los listos) y consumidores conectados.
    public async Task<(uint Ready, uint Consumers)> StatsAsync(string queue)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var q = await channel.QueueDeclarePassiveAsync(queue);
        return (q.MessageCount, q.ConsumerCount);
    }

    /// Publica bytes tal cual al exchange de pedidos (para probar mensajes que la API nunca mandaria).
    public async Task PublishRawAsync(byte[] body)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.BasicPublishAsync(RabbitTopology.Exchange, RabbitTopology.RoutingKey, body);
    }

    /// Busca un archivo del repositorio subiendo desde la carpeta bin del test.
    private static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"No se encontro {Path.Combine(parts)} subiendo desde {AppContext.BaseDirectory}");
    }
}
