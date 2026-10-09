using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers Postgres (<c>postgres:18-alpine</c>, the production major) for whatsapp-works-for-real
/// block B: one container, and a fresh database per test built from the REAL embedded
/// <c>Migrations/*.sql</c> resources — either up to (not including) <c>020_MessagesExternalIdUnique.sql</c>,
/// so a test can seed the duplicate provider ids the migration must collapse, or through every migration,
/// so the store runs against the production unique partial index.
/// </summary>
public sealed class MessageCorrelationFixture : IAsyncLifetime
{
    public const string Migration020 = "020_MessagesExternalIdUnique.sql";

    private const string ResourcePrefix = "Verbara.Platform.Storage.Postgres.Migrations.";

    private IContainer? _container;
    private readonly List<NpgsqlDataSource> _dataSources = [];

    private string ConnectionStringFor(string database) =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        $"Database={database};Username=postgres;Password=postgres";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:18-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    // `-h 127.0.0.1` forces the readiness probe over TCP (see MigrationsFixture).
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var ds in _dataSources)
            await ds.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>A new database with every migration applied, or only those before 020.</summary>
    public async Task<NpgsqlDataSource> CreateDatabaseAsync(bool before020 = false)
    {
        var name = $"msg_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(ConnectionStringFor("postgres")))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();

        var ds = NpgsqlDataSource.Create(ConnectionStringFor(name));
        _dataSources.Add(ds);

        foreach (var (file, sql) in Migrations())
        {
            if (before020 && string.CompareOrdinal(file, Migration020) >= 0)
                break;
            await using var cmd = ds.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        return ds;
    }

    /// <summary>Runs one embedded migration inside a transaction, as DatabaseMigrationService does.</summary>
    public static async Task ApplyMigrationAsync(NpgsqlDataSource ds, string file)
    {
        var sql = Migrations().Single(m => m.File == file).Sql;
        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public static async Task<long> ScalarAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<bool> IndexExistsAsync(NpgsqlDataSource ds, string index)
    {
        await using var cmd = ds.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = @Name)");
        cmd.Parameters.Add(new NpgsqlParameter("Name", index));
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Inserts a raw message row, bypassing the store (to seed what an older release wrote).</summary>
    public static async Task InsertRawAsync(
        NpgsqlDataSource ds, string tenantId, string messageId, string? externalId, DateTimeOffset createdAt,
        int direction = 0)
    {
        await using var cmd = ds.CreateCommand(
            "INSERT INTO messages (message_id, conversation_id, tenant_id, direction, channel, content, " +
            "delivery_status, external_message_id, created_at) VALUES (@MessageId, 'c-1', @TenantId, @Direction, 1, " +
            "'{\"blocks\": []}'::jsonb, 2, @ExternalId, @CreatedAt)");
        cmd.Parameters.Add(new NpgsqlParameter("MessageId", messageId));
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("Direction", NpgsqlTypes.NpgsqlDbType.Integer) { Value = direction });
        cmd.Parameters.Add(new NpgsqlParameter("ExternalId", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)externalId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("CreatedAt", createdAt));
        await cmd.ExecuteNonQueryAsync();
    }

    private static IEnumerable<(string File, string Sql)> Migrations()
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[ResourcePrefix.Length..], reader.ReadToEnd());
        }
    }
}
