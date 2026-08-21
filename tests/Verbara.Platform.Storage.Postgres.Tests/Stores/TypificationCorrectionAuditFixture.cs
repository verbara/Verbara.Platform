using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for
/// <see cref="PostgresTypificationCorrectionAuditWriterTests"/> (audit-trail-integrity-fixes,
/// fix 1). Spins up <c>postgres:16-alpine</c> with the three tables the atomic writer spans —
/// <c>typification_submissions</c>, <c>typification_submission_corrections</c>, and
/// <c>audit_entries</c> — exactly as declared in the consolidated 001_Baseline.sql, so the
/// single-transaction commit (and its rollback-on-conflict behaviour) is exercised against a real
/// DB rather than the InMemory mirror alone.
/// </summary>
public sealed class TypificationCorrectionAuditFixture : IAsyncLifetime
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

    /// <summary>Truncate the three tables between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "TRUNCATE typification_submissions, typification_submission_corrections, audit_entries " +
            "RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Run an arbitrary read directly — used to assert nothing was committed on rollback.</summary>
    public async Task<int> CountRowsAsync(string table)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    // typification_submissions + typification_submission_corrections DDL copied verbatim from the
    // consolidated 001_Baseline.sql (migrations 014 + 015); audit_entries copied verbatim from the
    // consolidated baseline (including the normalization columns — no staged migration steps
    // needed here since the writer only ever targets the FINAL shape).
    private const string SchemaSql = """
        CREATE TABLE typification_submissions (
            tenant_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
            agent_id TEXT NOT NULL, schema_id TEXT NOT NULL, schema_version INT NOT NULL,
            selected_node_path JSONB NOT NULL DEFAULT '[]', leaf_node_id TEXT NOT NULL,
            field_values JSONB NOT NULL DEFAULT '{}', notes TEXT,
            ai_suggested BOOLEAN NOT NULL DEFAULT false, ai_confidence DOUBLE PRECISION, ai_accepted BOOLEAN,
            source TEXT NOT NULL DEFAULT 'Manual', duration_ms BIGINT NOT NULL DEFAULT 0,
            completed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            suggested_leaf_node_id TEXT NULL, suggested_node_path JSONB NULL,
            autonomous_actor_id TEXT NULL,
            correction_state SMALLINT NOT NULL DEFAULT 0,
            corrected_at TIMESTAMPTZ NULL,
            PRIMARY KEY (tenant_id, conversation_id)
        );

        CREATE TABLE typification_submission_corrections (
            tenant_id             TEXT        NOT NULL,
            conversation_id       TEXT        NOT NULL,
            corrected_leaf_node_id TEXT       NOT NULL,
            corrected_node_path   JSONB       NOT NULL,
            corrected_by_user_id  TEXT        NOT NULL,
            corrected_at          TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (tenant_id, conversation_id)
        );

        CREATE TABLE audit_entries (
            entry_id TEXT NOT NULL PRIMARY KEY,
            tenant_id TEXT NOT NULL, action TEXT NOT NULL,
            entity_type TEXT NOT NULL, entity_id TEXT NOT NULL,
            performed_by TEXT, details JSONB, occurred_at TIMESTAMPTZ NOT NULL,
            impersonator_id TEXT,
            category TEXT NOT NULL DEFAULT 'config',
            severity TEXT NOT NULL DEFAULT 'info',
            actor_type TEXT NOT NULL DEFAULT 'system',
            before_json JSONB,
            after_json JSONB,
            integrity_hash TEXT,
            retain_until TIMESTAMPTZ NULL,
            CONSTRAINT audit_entries_severity_check
                CHECK (severity IN ('info', 'warn', 'warning', 'error', 'critical')),
            CONSTRAINT audit_entries_category_check
                CHECK (category IN ('auth', 'billing', 'config', 'tenant', 'security',
                                    'impersonation', 'retention', 'data', 'rbac',
                                    'data_access', 'admin', 'conversations', 'queues',
                                    'reports', 'operational', 'license')),
            CONSTRAINT audit_entries_actor_type_check
                CHECK (actor_type IN ('user', 'system', 'impersonator', 'service-account', 'api_key', 'ai'))
        );
        """;
}
