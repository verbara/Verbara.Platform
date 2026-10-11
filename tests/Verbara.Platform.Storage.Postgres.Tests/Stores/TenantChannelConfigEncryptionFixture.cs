using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed fixture for the channel credential encryption-at-rest suite
/// (whatsapp-works-for-real 6.4, design D15). Creates exactly the production
/// <c>tenant_channel_configs</c> table (001_Baseline) and an ephemeral DataProtection keyring
/// scoped to the fixture lifetime, the shape of <see cref="UserMfaEncryptionFixture"/>.
/// </summary>
public sealed class TenantChannelConfigEncryptionFixture : IAsyncLifetime
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
                    // TCP probe, not the socket: see UserMfaEncryptionFixture for the readiness race.
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();

        await _container.StartAsync();

        DataSource = NpgsqlDataSource.Create(ConnectionString);

        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Verbara.Platform.Storage.Postgres.Tests");
        DataProtection = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();

        await EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>(Re)creates the table; idempotent so the missing-table test can hand it back.</summary>
    public async Task EnsureSchemaAsync() => await ExecuteAsync(SchemaSql);

    /// <summary>Drops the table so a migrator run hits SQLSTATE 42P01. Restore with <see cref="EnsureSchemaAsync"/>.</summary>
    public async Task DropTableAsync() => await ExecuteAsync("DROP TABLE IF EXISTS tenant_channel_configs");

    public async Task ResetAsync() => await ExecuteAsync("TRUNCATE tenant_channel_configs");

    /// <summary>The raw <c>credentials</c> jsonb as text, bypassing the store.</summary>
    public async Task<string?> ReadRawCredentialsAsync(string tenantId, int channel)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT credentials::text FROM tenant_channel_configs WHERE tenant_id = @t AND channel = @c";
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Integer) { Value = channel });
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    /// <summary>
    /// Writes a row with <paramref name="credentialsJson"/> verbatim, bypassing the store's
    /// protect-on-write path — the legacy plaintext condition the migrator cleans up.
    /// </summary>
    public async Task WriteRawAsync(string tenantId, int channel, string credentialsJson)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tenant_channel_configs (tenant_id, channel, credentials, is_active) " +
            "VALUES (@t, @c, @j::jsonb, true) " +
            "ON CONFLICT (tenant_id, channel) DO UPDATE SET credentials = EXCLUDED.credentials";
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Integer) { Value = channel });
        cmd.Parameters.Add(new NpgsqlParameter("j", NpgsqlDbType.Text) { Value = credentialsJson });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // Verbatim from Migrations/001_Baseline.sql.
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS tenant_channel_configs (
            tenant_id TEXT NOT NULL,
            channel INTEGER NOT NULL,
            credentials JSONB NOT NULL DEFAULT '{}',
            is_active BOOLEAN NOT NULL DEFAULT true,
            PRIMARY KEY (tenant_id, channel)
        );
        """;
}
