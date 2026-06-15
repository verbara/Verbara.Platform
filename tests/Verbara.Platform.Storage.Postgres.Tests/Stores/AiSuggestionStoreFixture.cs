using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresAiSuggestionStoreTests"/>.
/// Spins up <c>postgres:16-alpine</c> with the DDL from migration
/// <c>004_typification_ai_suggestions.sql</c> so JSONB persistence
/// (suggested_node_path, suggested_field_values), nullable reconciliation fields
/// (committed_leaf_node_id, accepted), and the accuracy aggregation query can be
/// exercised against a real database.
/// </summary>
public sealed class AiSuggestionStoreFixture : IAsyncLifetime
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
                Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "postgres"))
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

    /// <summary>Truncate the suggestion table between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE typification_ai_suggestions RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    // DDL — mirrors 004_typification_ai_suggestions.sql.
    private const string SchemaSql = """
        CREATE TABLE typification_ai_suggestions (
            id                      TEXT             NOT NULL,
            tenant_id               TEXT             NOT NULL,
            conversation_id         TEXT             NOT NULL,
            schema_id               TEXT             NOT NULL,
            schema_version          INT              NOT NULL,
            suggested_leaf_node_id  TEXT             NOT NULL,
            suggested_node_path     JSONB            NOT NULL DEFAULT '[]',
            suggested_field_values  JSONB            NOT NULL DEFAULT '{}',
            confidence              DOUBLE PRECISION NOT NULL,
            sentiment               TEXT,
            model_id                TEXT             NOT NULL,
            prompt_version          TEXT             NOT NULL,
            created_at              TIMESTAMPTZ      NOT NULL,
            committed_leaf_node_id  TEXT,
            accepted                BOOLEAN,
            PRIMARY KEY (id)
        );

        CREATE INDEX idx_ai_suggestions_conversation
            ON typification_ai_suggestions (tenant_id, conversation_id, created_at DESC);

        CREATE INDEX idx_ai_suggestions_schema_accuracy
            ON typification_ai_suggestions (tenant_id, schema_id, created_at DESC);
        """;
}
