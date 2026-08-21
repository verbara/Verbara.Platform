using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for
/// <see cref="PostgresConversationStoreVoiceLinkTests"/>. Spins up
/// <c>postgres:16-alpine</c> with the final folded <c>conversations</c> table from
/// the consolidated 001_Baseline.sql (incl. the <c>voice_linked_id</c> column + partial
/// unique index and the <c>queue_priority</c> column), so the per-call voice
/// idempotency constraint can be exercised against a real DB.
/// Avoids dragging in the full platform schema.
/// </summary>
public sealed class ConversationVoiceLinkFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    // `-h 127.0.0.1` forces the readiness probe over TCP. The
                    // official entrypoint runs initdb against a *temporary*
                    // server started with `listen_addresses=''`, so a
                    // socket-only `pg_isready -U postgres` greens for a window
                    // while nothing is listening on 5432 yet — Testcontainers
                    // then reports ready, docker's port proxy accepts the
                    // mapped-port connection and immediately closes it, and
                    // Npgsql surfaces "Attempted to read past the end of the
                    // stream" mid-authentication. Probing TCP skips that
                    // window (measured: socket ready ~4s before TCP).
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

    /// <summary>Truncate test tables between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE conversations RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    // conversations DDL — subset of the consolidated 001_Baseline.sql (final folded shape:
    // voice_linked_id + queue_priority included; the legacy wrap_up column was dropped).
    private const string SchemaSql = """
        CREATE TABLE conversations (
            conversation_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            contact_id TEXT NOT NULL,
            channel INTEGER NOT NULL,
            state INTEGER NOT NULL,
            owner_kind INTEGER,
            owner_id TEXT,
            case_id TEXT,
            metadata JSONB NOT NULL DEFAULT '{}',
            created_at TIMESTAMPTZ NOT NULL,
            closed_at TIMESTAMPTZ,
            updated_at TIMESTAMPTZ,
            created_by TEXT,
            updated_by TEXT,
            voice_linked_id TEXT,
            queue_priority INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (tenant_id, conversation_id)
        );
        CREATE INDEX idx_conversations_contact ON conversations (tenant_id, contact_id, state);
        CREATE UNIQUE INDEX uq_conversations_voice_linked_id
            ON conversations (tenant_id, voice_linked_id)
            WHERE voice_linked_id IS NOT NULL;
        """;
}
