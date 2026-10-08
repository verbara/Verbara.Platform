using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresContactStoreFindByAddressTests"/>.
/// Spins up <c>postgres:18-alpine</c> (the production major) with the <c>contacts</c> DDL
/// mirrored from migration <c>001_Baseline.sql</c>, so <c>addresses</c> is a real JSONB column
/// and the address lookup runs against the same SQL engine it does in production.
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
        cmd.CommandText = "TRUNCATE contacts";
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
}
