using MercadoYa.Payments;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MercadoYa.Tests;

/// BD `payments` real (PostgreSQL 16 descartable) con el esquema de infra/postgres/init/04-payments-schema.sql.
/// Es otra base, como en produccion (database per service): por eso tiene su propio contenedor y su propia coleccion.
public sealed class PaymentsDbFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16").WithDatabase("payments").Build();

    public string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        // El script empieza con `\connect payments` (comando de psql, no SQL): aca ya estamos conectados a esa base.
        var sql = string.Join('\n', (await File.ReadAllLinesAsync(RepoFile("infra", "postgres", "init", "04-payments-schema.sql")))
            .Where(l => !l.TrimStart().StartsWith('\\')));
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    public PaymentsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task<List<PaymentRecord>> RecordsOfAsync(Guid orderId)
    {
        await using var db = NewContext();
        return await db.Payments.AsNoTracking().Where(p => p.OrderId == orderId).ToListAsync();
    }

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

[CollectionDefinition("payments-db")]
public sealed class PaymentsDbCollection : ICollectionFixture<PaymentsDbFixture>;
