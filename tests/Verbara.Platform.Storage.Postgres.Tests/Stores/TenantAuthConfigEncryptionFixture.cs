using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed fixture for the ADMIN-001 OIDC client-secret
/// encryption regression suite. Spins up <c>postgres:16-alpine</c> and
/// creates the minimum schema needed for
/// <c>PostgresTenantAuthConfigStore</c>: a one-column <c>tenants</c> table
/// (FK target, mirrors the convention from <c>IpAllowlistFixture</c>) and the
/// columns of <c>tenant_auth_config</c> that <c>GetAsync</c>/<c>SaveAsync</c>
/// touch. We do NOT replay the production migration ledger here — that would
/// drag in 24+ migration files for what is effectively a one-table test.
///
/// DataProtection runs with ephemeral in-memory keys: each fixture instance
/// gets its own keyring, so encrypted ciphertext is per-test-class
/// deterministic and Unprotect succeeds across the same fixture lifetime.
/// </summary>
public sealed class TenantAuthConfigEncryptionFixture : IAsyncLifetime
{
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
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres"))
            .Build();

        await _container.StartAsync();

        DataSource = NpgsqlDataSource.Create(ConnectionString);

        // Ephemeral DataProtection keyring scoped to the fixture lifetime.
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
        cmd.CommandText = "TRUNCATE tenant_auth_config, tenants RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SeedTenantAsync(string tenantId)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO tenants (tenant_id) VALUES (@t) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("t", tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Reads the raw <c>oidc_client_secret</c> column value bypassing the
    /// store entirely. Used by tests to assert that the byte sequence on
    /// disk is encrypted ciphertext, not plaintext.
    /// </summary>
    public async Task<string?> ReadRawSecretAsync(string tenantId)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT oidc_client_secret FROM tenant_auth_config WHERE tenant_id = @t";
        cmd.Parameters.AddWithValue("t", tenantId);
        var result = await cmd.ExecuteScalarAsync();
        return result == DBNull.Value ? null : (string?)result;
    }

    /// <summary>
    /// Inserts a row with the given <paramref name="oidcClientSecret"/>
    /// value WITHOUT going through the store — used to set up the legacy
    /// plaintext condition that <c>OidcClientSecretEncryptionMigrator</c>
    /// is designed to clean up.
    /// </summary>
    public async Task InsertRawAsync(string tenantId, string? oidcClientSecret)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenant_auth_config (
                tenant_id, mfa_policy, mfa_required_roles, password_min_length,
                password_require_uppercase, password_require_number, password_require_special,
                lockout_threshold, lockout_duration_minutes, session_idle_timeout_minutes,
                session_absolute_timeout_hours, oidc_enabled, oidc_authority, oidc_client_id,
                oidc_client_secret, oidc_auto_create_users, oidc_default_role,
                impersonation_max_concurrent_sessions, impersonation_auto_timeout_minutes,
                ip_allowlist_enabled, updated_at)
            VALUES (
                @t, 'optional', '{}', 12,
                true, true, false,
                5, 15, 30,
                12, true, 'https://idp.test', 'client',
                @s, true, 'Agent',
                3, 240,
                false, now())
            ON CONFLICT (tenant_id) DO UPDATE SET oidc_client_secret = EXCLUDED.oidc_client_secret
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        if (oidcClientSecret is null)
            cmd.Parameters.AddWithValue("s", DBNull.Value);
        else
            cmd.Parameters.AddWithValue("s", oidcClientSecret);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS tenants (
            tenant_id TEXT PRIMARY KEY
        );

        CREATE TABLE IF NOT EXISTS tenant_auth_config (
            tenant_id TEXT PRIMARY KEY REFERENCES tenants(tenant_id) ON DELETE CASCADE,
            mfa_policy TEXT NOT NULL DEFAULT 'optional',
            mfa_required_roles TEXT[] NOT NULL DEFAULT '{}',
            password_min_length INT NOT NULL DEFAULT 12,
            password_require_uppercase BOOLEAN NOT NULL DEFAULT true,
            password_require_number BOOLEAN NOT NULL DEFAULT true,
            password_require_special BOOLEAN NOT NULL DEFAULT false,
            lockout_threshold INT NOT NULL DEFAULT 5,
            lockout_duration_minutes INT NOT NULL DEFAULT 15,
            session_idle_timeout_minutes INT NOT NULL DEFAULT 30,
            session_absolute_timeout_hours INT NOT NULL DEFAULT 12,
            oidc_enabled BOOLEAN NOT NULL DEFAULT false,
            oidc_authority TEXT,
            oidc_client_id TEXT,
            oidc_client_secret TEXT,
            oidc_auto_create_users BOOLEAN NOT NULL DEFAULT true,
            oidc_default_role TEXT NOT NULL DEFAULT 'Agent',
            impersonation_max_concurrent_sessions INT NOT NULL DEFAULT 3,
            impersonation_auto_timeout_minutes INT NOT NULL DEFAULT 240,
            ip_allowlist_enabled BOOLEAN NOT NULL DEFAULT false,
            updated_at TIMESTAMPTZ
        );
        """;
}
