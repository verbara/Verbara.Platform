using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for
/// <see cref="PostgresConversationStoreVoiceLinkTests"/>. Spins up
/// <c>postgres:16-alpine</c> with the <c>conversations</c> table from migration 001
/// plus the <c>voice_linked_id</c> column + partial unique index from migration 027,
/// so the per-call voice idempotency constraint can be exercised against a real DB.
/// Avoids dragging in the full 20+ migration platform schema.
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
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres"))
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

    // conversations DDL from 001_InitialSchema.sql + the 027_VoiceConversationLink delta.
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
            PRIMARY KEY (tenant_id, conversation_id)
        );
        CREATE INDEX idx_conversations_contact ON conversations (tenant_id, contact_id, state);
        CREATE UNIQUE INDEX uq_conversations_voice_linked_id
            ON conversations (tenant_id, voice_linked_id)
            WHERE voice_linked_id IS NOT NULL;
        """;
}
