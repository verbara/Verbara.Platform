using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for
/// <see cref="PostgresTenantIpAllowlistStoreTests"/>. Spins up
/// <c>postgres:16-alpine</c> and creates a minimal schema:
/// <c>tenants(tenant_id TEXT PK)</c> + the full <c>tenant_ip_allowlist</c>
/// table from migration 023. Avoids dragging in the 20+ migration
/// platform schema.
/// </summary>
public sealed class IpAllowlistFixture : IAsyncLifetime
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
        cmd.CommandText = "TRUNCATE tenant_ip_allowlist, tenants RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    private const string SchemaSql = """
        CREATE TABLE tenants (
            tenant_id TEXT PRIMARY KEY
        );

        CREATE TABLE tenant_ip_allowlist (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
            cidr CIDR NOT NULL,
            description TEXT,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by_user_id TEXT,
            CONSTRAINT cidr_per_tenant_unique UNIQUE (tenant_id, cidr)
        );

        CREATE INDEX idx_tenant_ip_allowlist_tenant ON tenant_ip_allowlist(tenant_id);
        """;
}
