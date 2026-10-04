using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresMessageStoreJsonbTests"/>.
/// Spins up <c>postgres:18-alpine</c> (the production major) with the <c>messages</c> DDL
/// mirrored from migration <c>001_Baseline.sql</c>, so the <c>content</c> JSONB column
/// re-orders object keys exactly as it does in production.
/// </summary>
public sealed class MessageStoreFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:18-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    // `-h 127.0.0.1` forces the readiness probe over TCP — see
                    // ConversationVoiceLinkFixture for why a socket-only probe is unsafe.
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();

        await _container.StartAsync();

        DataSource = NpgsqlDataSource.Create(ConnectionString);

        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Truncate the table between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE messages";
        await cmd.ExecuteNonQueryAsync();
    }

    // messages DDL — verbatim subset of the consolidated 001_Baseline.sql.
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS messages (
            message_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            direction INTEGER NOT NULL,
            channel INTEGER NOT NULL,
            sender_id TEXT,
            content JSONB NOT NULL,
            delivery_status INTEGER NOT NULL DEFAULT 0,
            external_message_id TEXT,
            created_at TIMESTAMPTZ NOT NULL,
            delivered_at TIMESTAMPTZ,
            read_at TIMESTAMPTZ,
            updated_at TIMESTAMPTZ,
            created_by TEXT,
            updated_by TEXT,
            PRIMARY KEY (tenant_id, message_id)
        );
        CREATE INDEX IF NOT EXISTS idx_messages_conversation ON messages (tenant_id, conversation_id, created_at);
        CREATE INDEX IF NOT EXISTS idx_messages_external ON messages (tenant_id, external_message_id);
        """;
}
