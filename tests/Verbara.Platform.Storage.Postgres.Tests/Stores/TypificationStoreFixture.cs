using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for the typification stores
/// (<see cref="PostgresTypificationStoresTests"/>). Spins up <c>postgres:16-alpine</c>
/// with the three typification tables exactly as declared in the consolidated
/// 001_Baseline.sql (<c>typification_schemas</c>, <c>typification_bindings</c>,
/// <c>typification_submissions</c>), so the JSONB <c>definition</c> /
/// <c>selected_node_path</c> / <c>field_values</c> round-trips and the
/// version/scope projections can be exercised against a real DB. Avoids dragging
/// in the full platform schema.
/// </summary>
public sealed class TypificationStoreFixture : IAsyncLifetime
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

    /// <summary>Truncate the typification tables between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "TRUNCATE typification_schemas, typification_bindings, typification_submissions, reason_hints RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    // typification DDL — copied verbatim from the consolidated 001_Baseline.sql.
    private const string SchemaSql = """
        CREATE TABLE typification_schemas (
            tenant_id TEXT NOT NULL, schema_id TEXT NOT NULL,
            name TEXT NOT NULL, version INT NOT NULL DEFAULT 1,
            is_published BOOLEAN NOT NULL DEFAULT false,
            max_depth INT NOT NULL DEFAULT 5,
            definition JSONB NOT NULL DEFAULT '{}',
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ,
            PRIMARY KEY (tenant_id, schema_id, version)
        );

        CREATE TABLE typification_bindings (
            tenant_id TEXT NOT NULL, binding_id TEXT NOT NULL,
            scope TEXT NOT NULL, scope_ref TEXT,
            schema_id TEXT NOT NULL, subtree_root_node_id TEXT,
            priority INT NOT NULL DEFAULT 0,
            ai_config_override JSONB NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, binding_id)
        );
        CREATE INDEX idx_typification_bindings_scope ON typification_bindings (tenant_id, scope, scope_ref);

        CREATE TABLE typification_submissions (
            tenant_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
            agent_id TEXT NOT NULL, schema_id TEXT NOT NULL, schema_version INT NOT NULL,
            selected_node_path JSONB NOT NULL DEFAULT '[]', leaf_node_id TEXT NOT NULL,
            field_values JSONB NOT NULL DEFAULT '{}', notes TEXT,
            ai_suggested BOOLEAN NOT NULL DEFAULT false, ai_confidence DOUBLE PRECISION, ai_accepted BOOLEAN,
            source TEXT NOT NULL DEFAULT 'Manual', duration_ms BIGINT NOT NULL DEFAULT 0,
            completed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, conversation_id)
        );
        CREATE INDEX idx_typification_submissions_leaf ON typification_submissions (tenant_id, leaf_node_id, completed_at DESC);
        CREATE INDEX idx_typification_submissions_completed ON typification_submissions (tenant_id, completed_at DESC);

        CREATE TABLE reason_hints (
            tenant_id   TEXT    NOT NULL,
            hint_id     TEXT    NOT NULL,
            scope       TEXT    NOT NULL,
            scope_ref   TEXT    NOT NULL,
            reason_path TEXT    NOT NULL,
            priority    INT     NOT NULL DEFAULT 0,
            is_active   BOOLEAN NOT NULL DEFAULT TRUE,
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, hint_id)
        );
        CREATE INDEX idx_reason_hints_scope ON reason_hints (tenant_id, scope, scope_ref);
        """;
}
