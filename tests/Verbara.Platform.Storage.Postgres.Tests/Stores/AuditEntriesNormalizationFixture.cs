using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for
/// <see cref="AuditEntriesNormalizationTests"/>. Spins up
/// <c>postgres:16-alpine</c> and applies the audit_entries shape from
/// migration 001 plus the normalization columns + check constraints + filter
/// indexes from migration 021 (R5.3 Phase A Task A.1, ADR-0006).
/// </summary>
/// <remarks>
/// SQL is inlined rather than running the full 21-migration pipeline so the
/// fixture exercises only the surface this test suite cares about (audit
/// store round-trip + index plan check + CHECK constraint enforcement).
/// </remarks>
public sealed class AuditEntriesNormalizationFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

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

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql + MigrationV021Sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>Truncate audit_entries between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE audit_entries";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Run an arbitrary INSERT directly — used to seed bad rows for CHECK tests.</summary>
    public async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // Subset of 001_InitialSchema.sql — audit_entries table + impersonator_id
    // column from 011. Migration 021 shape is applied separately below.
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS audit_entries (
            entry_id TEXT NOT NULL PRIMARY KEY,
            tenant_id TEXT NOT NULL,
            action TEXT NOT NULL,
            entity_type TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            performed_by TEXT,
            details JSONB,
            occurred_at TIMESTAMPTZ NOT NULL,
            impersonator_id TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_audit_entity ON audit_entries (tenant_id, entity_type, entity_id, occurred_at DESC);
        CREATE INDEX IF NOT EXISTS idx_audit_time ON audit_entries (tenant_id, occurred_at DESC);
        """;

    // Mirrors 021_AuditEntriesNormalize.sql exactly. Kept inline so the
    // fixture can apply it even when the embedded resource isn't loaded
    // through the production SchemaMigrator path.
    private const string MigrationV021Sql = """

        BEGIN;
        ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS category       TEXT  NULL;
        ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS severity       TEXT  NULL;
        ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS actor_type     TEXT  NULL;
        ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS before_json    JSONB NULL;
        ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS after_json     JSONB NULL;
        ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS integrity_hash TEXT  NULL;

        ALTER TABLE audit_entries ALTER COLUMN category   SET DEFAULT 'config';
        ALTER TABLE audit_entries ALTER COLUMN severity   SET DEFAULT 'info';
        ALTER TABLE audit_entries ALTER COLUMN actor_type SET DEFAULT 'system';

        UPDATE audit_entries SET category   = 'config' WHERE category   IS NULL;
        UPDATE audit_entries SET severity   = 'info'   WHERE severity   IS NULL;
        UPDATE audit_entries SET actor_type = 'system' WHERE actor_type IS NULL;

        ALTER TABLE audit_entries ALTER COLUMN category   SET NOT NULL;
        ALTER TABLE audit_entries ALTER COLUMN severity   SET NOT NULL;
        ALTER TABLE audit_entries ALTER COLUMN actor_type SET NOT NULL;

        ALTER TABLE audit_entries DROP CONSTRAINT IF EXISTS audit_entries_severity_check;
        ALTER TABLE audit_entries
            ADD CONSTRAINT audit_entries_severity_check
            CHECK (severity IN ('info', 'warn', 'warning', 'error', 'critical'));

        ALTER TABLE audit_entries DROP CONSTRAINT IF EXISTS audit_entries_category_check;
        ALTER TABLE audit_entries
            ADD CONSTRAINT audit_entries_category_check
            CHECK (category IN ('auth', 'billing', 'config', 'tenant', 'security',
                                'impersonation', 'retention', 'data', 'rbac',
                                'data_access', 'admin'));

        ALTER TABLE audit_entries DROP CONSTRAINT IF EXISTS audit_entries_actor_type_check;
        ALTER TABLE audit_entries
            ADD CONSTRAINT audit_entries_actor_type_check
            CHECK (actor_type IN ('user', 'system', 'impersonator', 'service-account', 'api_key'));

        CREATE INDEX IF NOT EXISTS idx_audit_severity
            ON audit_entries (tenant_id, severity, occurred_at DESC);

        CREATE INDEX IF NOT EXISTS idx_audit_category
            ON audit_entries (tenant_id, category, occurred_at DESC);
        COMMIT;
        """;
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("AuditEntriesNormalization")]
public class AuditEntriesNormalizationCollection : ICollectionFixture<AuditEntriesNormalizationFixture>;
#pragma warning restore CA1711
