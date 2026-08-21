using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed fixture for the P2c.1 <c>TenantLlmConfigSeedMigrator</c> suite. Spins up
/// <c>postgres:16-alpine</c> and creates the minimum schema the migrator touches: a <c>tenants</c>
/// table with a <c>type</c> column (the operational-tenant discriminator) and the
/// <c>tenant_llm_config</c> table with every column the store reads (through migration 010's
/// <c>ai_source</c>). We do NOT replay the full migration ledger — this is a two-table seed test,
/// so columns added by later additive migrations must be mirrored here in lock-step with the store.
///
/// DataProtection runs with ephemeral in-memory keys scoped to the fixture lifetime so encrypted
/// ciphertext is deterministic across the same fixture and Unprotect round-trips.
/// </summary>
public sealed class TenantLlmConfigSeedFixture : IAsyncLifetime
{
    /// <summary>Matches <c>(int)Verbara.Sdk.Pro.MultiTenant.TenantType.Customer</c> — the operational tenant type.</summary>
    public const int CustomerTenantType = 2;

    /// <summary>A non-operational tenant type (Platform = 0) for the "not exactly one Customer" cases.</summary>
    public const int PlatformTenantType = 0;

    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres;SSL Mode=Disable";

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public IDataProtectionProvider DataProtection { get; private set; } = null!;

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

        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Verbara.Platform.Storage.Postgres.Tests");
        var provider = services.BuildServiceProvider();
        DataProtection = provider.GetRequiredService<IDataProtectionProvider>();

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

    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE tenant_llm_config, tenants RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SeedTenantAsync(string tenantId, int type)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO tenants (tenant_id, type) VALUES (@t, @ty) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("ty", type);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Inserts a tenant_llm_config row directly (used to set up the "row already exists" no-op case).</summary>
    public async Task InsertConfigRawAsync(string tenantId, string model)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenant_llm_config
                (tenant_id, provider_type, model, api_key_encrypted, api_key_last4,
                 provider_settings, enabled, created_at, updated_at)
            VALUES (@t, 'OpenAiCompatible', @m, NULL, NULL, '{}'::jsonb, true, now(), now())
            ON CONFLICT (tenant_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("m", model);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountConfigRowsAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tenant_llm_config";
        var result = await cmd.ExecuteScalarAsync();
        return (int)(long)result!;
    }

    /// <summary>Reads the raw (encrypted) api_key column for a tenant, bypassing the store.</summary>
    public async Task<string?> ReadRawKeyAsync(string tenantId)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT api_key_encrypted FROM tenant_llm_config WHERE tenant_id = @t";
        cmd.Parameters.AddWithValue("t", tenantId);
        var result = await cmd.ExecuteScalarAsync();
        return result == DBNull.Value ? null : (string?)result;
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS tenants (
            tenant_id TEXT PRIMARY KEY,
            type      INTEGER NOT NULL DEFAULT 2
        );

        CREATE TABLE IF NOT EXISTS tenant_llm_config (
            tenant_id          TEXT        NOT NULL,
            provider_type      TEXT        NOT NULL,
            model              TEXT        NOT NULL,
            api_key_encrypted  TEXT,
            api_key_last4      TEXT,
            provider_settings  JSONB       NOT NULL DEFAULT '{}',
            enabled            BOOLEAN     NOT NULL DEFAULT false,
            ai_source          TEXT        NOT NULL DEFAULT 'Byo',
            created_at         TIMESTAMPTZ NOT NULL,
            updated_at         TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (tenant_id)
        );
        """;
}
