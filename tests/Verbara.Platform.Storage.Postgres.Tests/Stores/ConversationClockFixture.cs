using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// conversations-close-after-inactivity (tasks 1.4, 1.5): Testcontainers Postgres (<c>postgres:18-alpine</c>, the
/// production major), one container and a fresh database per test built from the REAL embedded
/// <c>Migrations/*.sql</c> resources — either up to (not including) <see cref="Migration026"/>, so a test can seed
/// what an older release wrote and then run the migration, or through every migration.
/// </summary>
public sealed class ConversationClockFixture : IAsyncLifetime
{
    public const string Migration026 = "026_ConversationLastMessageAt.sql";

    private const string ResourcePrefix = "Verbara.Platform.Storage.Postgres.Migrations.";

    private IContainer? _container;
    private readonly List<NpgsqlDataSource> _dataSources = [];

    public IDataProtectionProvider DataProtection { get; private set; } = null!;

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

        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Verbara.Platform.Storage.Postgres.Tests");
        DataProtection = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    public async Task DisposeAsync()
    {
        foreach (var ds in _dataSources)
            await ds.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>A new database with every migration applied, or only those before 026.</summary>
    public async Task<NpgsqlDataSource> CreateDatabaseAsync(bool before026 = false)
    {
        var name = $"clk_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(ConnectionStringFor("postgres")))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();

        var ds = NpgsqlDataSource.Create(ConnectionStringFor(name));
        _dataSources.Add(ds);

        foreach (var (file, sql) in Migrations())
        {
            if (before026 && string.CompareOrdinal(file, Migration026) >= 0)
                break;
            await using var cmd = ds.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        return ds;
    }

    /// <summary>Runs one embedded migration inside a transaction, as DatabaseMigrationService does.</summary>
    public static async Task ApplyMigrationAsync(NpgsqlDataSource ds, string file)
    {
        var sql = MigrationSql(file);
        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    /// <summary>The text of one embedded migration.</summary>
    public static string MigrationSql(string file) => Migrations().Single(m => m.File == file).Sql;

    /// <summary>
    /// Inserts a conversation row the way an older release did: every column but <c>last_message_at</c>.
    /// </summary>
    public static async Task InsertLegacyConversationAsync(
        NpgsqlDataSource ds, string tenantId, string conversationId, int state, DateTimeOffset createdAt,
        int channel = 1)
    {
        await using var cmd = ds.CreateCommand(
            "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, metadata, created_at) " +
            "VALUES (@Id, @TenantId, 'contact-1', @Channel, @State, '{}'::jsonb, @CreatedAt)");
        cmd.Parameters.Add(new NpgsqlParameter("Id", conversationId));
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("Channel", NpgsqlDbType.Integer) { Value = channel });
        cmd.Parameters.Add(new NpgsqlParameter("State", NpgsqlDbType.Integer) { Value = state });
        cmd.Parameters.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = createdAt });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Inserts a raw message row (direction / delivery status as integers), bypassing the store.</summary>
    public static async Task InsertMessageAsync(
        NpgsqlDataSource ds, string tenantId, string conversationId, int direction, int deliveryStatus,
        DateTimeOffset createdAt)
    {
        await using var cmd = ds.CreateCommand(
            "INSERT INTO messages (message_id, conversation_id, tenant_id, direction, channel, content, " +
            "delivery_status, created_at) VALUES (@MessageId, @ConversationId, @TenantId, @Direction, 1, " +
            "'{\"blocks\": []}'::jsonb, @Status, @CreatedAt)");
        cmd.Parameters.Add(new NpgsqlParameter("MessageId", $"m-{Guid.NewGuid():N}"));
        cmd.Parameters.Add(new NpgsqlParameter("ConversationId", conversationId));
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("Direction", NpgsqlDbType.Integer) { Value = direction });
        cmd.Parameters.Add(new NpgsqlParameter("Status", NpgsqlDbType.Integer) { Value = deliveryStatus });
        cmd.Parameters.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = createdAt });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The stored <c>last_message_at</c> of one conversation (null when the row or the value is absent).</summary>
    public static async Task<DateTimeOffset?> LastMessageAtAsync(NpgsqlDataSource ds, string tenantId, string conversationId)
    {
        await using var cmd = ds.CreateCommand(
            "SELECT last_message_at FROM conversations WHERE tenant_id = @TenantId AND conversation_id = @Id");
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("Id", conversationId));
        var value = await cmd.ExecuteScalarAsync();
        return value is DateTime dt ? new DateTimeOffset(dt.ToUniversalTime()) : null;
    }

    public static async Task ExecuteAsync(NpgsqlDataSource ds, string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(NpgsqlDataSource ds, string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
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
