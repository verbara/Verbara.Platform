using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresUsageRecordStoreTests"/>.
/// Spins up <c>postgres:16-alpine</c> with the <c>usage_records</c> DDL mirrored from
/// migration <c>001_Baseline.sql</c> (including the <c>metadata</c> JSONB column) so the
/// per-direction token breakdown aggregation can be exercised against a real database.
/// </summary>
public sealed class UsageRecordStoreFixture : IAsyncLifetime
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

    /// <summary>Truncate the usage_records table between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE usage_records RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    // DDL — mirrors usage_records in 001_Baseline.sql (incl. the metadata JSONB column).
    private const string SchemaSql = """
        CREATE TABLE usage_records (
            record_id    TEXT PRIMARY KEY,
            tenant_id    TEXT NOT NULL,
            usage_type   SMALLINT NOT NULL,
            quantity     NUMERIC(18,6) NOT NULL,
            unit         SMALLINT NOT NULL,
            channel      TEXT,
            reference_id TEXT,
            recorded_at  TIMESTAMPTZ NOT NULL,
            metadata     JSONB
        );

        CREATE INDEX idx_usage_tenant_period ON usage_records (tenant_id, recorded_at DESC);
        CREATE INDEX idx_usage_tenant_type ON usage_records (tenant_id, usage_type, recorded_at DESC);
        """;
}
