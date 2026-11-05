using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresContactStoreFindByAddressTests"/>.
/// Spins up <c>postgres:18-alpine</c> (the production major) with the <c>contacts</c> DDL
/// mirrored from migration <c>001_Baseline.sql</c>, so <c>addresses</c> is a real JSONB column
/// and the address lookup runs against the same SQL engine it does in production. It also holds
/// <c>audit_entries</c> in its migrated shape, then the embedded migration that admits the
/// <c>contact</c> category, for <see cref="PostgresContactAuditWriterTests"/>.
/// </summary>
public sealed class ContactStoreFixture : IAsyncLifetime
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
        cmd.CommandText = SchemaSql + AuditEntriesSql + ConversationClockFixture.MigrationSql(AuditContactCategoryMigration);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The migration that admits the audit category <c>contact</c> (contacts-writes-follow-their-permissions D17).</summary>
    public const string AuditContactCategoryMigration = "029_AuditContactCategory.sql";

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Truncate the tables between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE contacts, audit_entries";
        await cmd.ExecuteNonQueryAsync();
    }

    // contacts DDL — verbatim subset of the consolidated 001_Baseline.sql (no later migration
    // touches the table).
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS contacts (
            contact_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            first_name TEXT,
            last_name TEXT,
            company TEXT,
            segment TEXT,
            preferred_channel INTEGER,
            preferred_language TEXT,
            timezone TEXT,
            do_not_contact BOOLEAN NOT NULL DEFAULT false,
            addresses JSONB NOT NULL DEFAULT '[]',
            custom_fields JSONB NOT NULL DEFAULT '{}',
            channel_consent JSONB NOT NULL DEFAULT '{}',
            created_at TIMESTAMPTZ NOT NULL,
            updated_at TIMESTAMPTZ,
            created_by TEXT,
            updated_by TEXT,
            PRIMARY KEY (tenant_id, contact_id)
        );
        """;

    // audit_entries in the shape every migration before 029 leaves it (001_Baseline.sql, then 006's 'ai' actor and
    // 014's retain_until and nullable entity_id), as TypificationCorrectionAuditFixture declares it.
    private const string AuditEntriesSql = """
        CREATE TABLE IF NOT EXISTS audit_entries (
            entry_id TEXT NOT NULL PRIMARY KEY,
            tenant_id TEXT NOT NULL, action TEXT NOT NULL,
            entity_type TEXT NOT NULL, entity_id TEXT,
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
