using MercadoYa.Modules.Pedidos;
using Testcontainers.RabbitMq;
using Xunit;

namespace MercadoYa.Tests;

/// RabbitMQ real en un contenedor descartable (Testcontainers), con la topologia de infra/rabbitmq/definitions.json.
/// Se levanta una vez para los tests de la coleccion "rabbitmq" y se destruye al terminar. Requiere Docker andando.
/// Para correr solo los tests sin Docker: dotnet test --filter "Category!=Integracion"
public sealed class RabbitFixture : RabbitFixtureBase
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management")
        .WithUsername("mercadoya")
        .WithPassword("mercadoya_dev")
        .Build();

    public override RabbitMqOptions Options => new(_rabbit.Hostname, _rabbit.GetMappedPublicPort(5672), "mercadoya", "mercadoya_dev", "/");

    public override async Task InitializeAsync()
    {
        await _rabbit.StartAsync();
        await DeclareTopologyAsync();
    }

    public override Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();
}

[CollectionDefinition("rabbitmq")]
public sealed class RabbitCollection : ICollectionFixture<RabbitFixture>;
