using MercadoYa.Modules.Inventario;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MercadoYa.Tests;

/// PostgreSQL 16 real en un contenedor descartable (Testcontainers), con el esquema de infra/postgres/init/02-mercadoya-schema.sql.
/// Se levanta una vez para todos los tests de la coleccion "postgres" y se destruye al terminar. Requiere Docker andando.
/// Para correr solo los tests sin Docker: dotnet test --filter "Category!=Integracion"
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16").WithDatabase("mercadoya").Build();
    private int _categoryId;

    public string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await ExecAsync(await File.ReadAllTextAsync(RepoFile("infra", "postgres", "init", "02-mercadoya-schema.sql")));
        _categoryId = await ScalarAsync<int>("""INSERT INTO catalogo."Category" ("Name") VALUES ('Pruebas') RETURNING "Id" """);
    }

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    /// Contexto nuevo por operacion: asi cada llamada lee la base y no el cache del change tracker.
    public InventarioDbContext NewContext() =>
        new(new DbContextOptionsBuilder<InventarioDbContext>().UseNpgsql(ConnectionString).Options);

    /// Crea un producto propio del test (los tests no comparten productos, por eso pueden correr en cualquier orden).
    public Task<int> NewProductAsync(int stock, bool active = true) =>
        ScalarAsync<int>($"""
            INSERT INTO catalogo."Product" ("Name","PriceCents","Stock","CategoryId","Active")
            VALUES ('Producto {Guid.NewGuid():N}', 100000, {stock}, {_categoryId}, {active}) RETURNING "Id"
            """);

    public Task<int> StockOfAsync(int productId) =>
        ScalarAsync<int>($"""SELECT "Stock" FROM catalogo."Product" WHERE "Id" = {productId}""");

    public async Task<List<(int Delta, string Reason, Guid? OrderId)>> MovementsOfAsync(int productId)
    {
        await using var db = NewContext();
        return (await db.Movements.AsNoTracking().Where(m => m.ProductId == productId).OrderBy(m => m.Id).ToListAsync())
            .Select(m => (m.Delta, m.Reason, m.OrderId)).ToList();
    }

    public async Task<List<StockReservation>> ReservationsOfAsync(Guid orderId)
    {
        await using var db = NewContext();
        return await db.Reservations.AsNoTracking().Where(r => r.OrderId == orderId).OrderBy(r => r.ProductId).ToListAsync();
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
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

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
