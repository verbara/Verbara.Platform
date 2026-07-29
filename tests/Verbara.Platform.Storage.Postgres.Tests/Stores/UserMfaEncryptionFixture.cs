using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed fixture for the A7 (encrypt-mfa-secrets-at-rest) MFA
/// enrollment-material encryption regression suite. Spins up
/// <c>postgres:16-alpine</c> and creates the minimum schema needed for
/// <c>PostgresUserStore</c> and <c>UserMfaEncryptionMigrator</c>: a one-column
/// <c>tenants</c> table (FK target, mirrors the convention from
/// <c>TenantAuthConfigEncryptionFixture</c>) plus exactly the <c>users</c>
/// columns the store's <c>SelectColumns</c> list touches, keyed on
/// <c>(tenant_id, user_id)</c> so the store's UPSERT clause resolves. We do NOT
/// replay the production migration ledger here — that would drag in 24+
/// migration files for what is effectively a one-table test.
///
/// DataProtection runs with ephemeral in-memory keys: each fixture instance
/// gets its own keyring, so encrypted ciphertext is per-test-class
/// deterministic and Unprotect succeeds across the same fixture lifetime.
/// </summary>
public sealed class UserMfaEncryptionFixture : IAsyncLifetime
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

        // Ephemeral DataProtection keyring scoped to the fixture lifetime.
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Verbara.Platform.Storage.Postgres.Tests");
        var provider = services.BuildServiceProvider();
        DataProtection = provider.GetRequiredService<IDataProtectionProvider>();

        await EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>
    /// (Re)creates the minimal schema. Idempotent, so the missing-table test can
    /// drop <c>users</c> and hand the table back afterwards.
    /// </summary>
    public async Task EnsureSchemaAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Drops <c>users</c> so a migrator run hits SQLSTATE <c>42P01</c>
    /// (undefined_table) — the fresh-install boot order the migrator must treat
    /// as a silent no-op. Callers MUST restore the table with
    /// <see cref="EnsureSchemaAsync"/> afterwards.
    /// </summary>
    public async Task DropUsersTableAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS users";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE users, tenants RESTART IDENTITY CASCADE";
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
    /// Reads the raw <c>users.mfa_secret</c> column value bypassing the store
    /// entirely. Used by tests to assert that the byte sequence on disk is
    /// encrypted ciphertext, not the plaintext TOTP shared secret.
    /// </summary>
    public async Task<string?> ReadRawMfaSecretAsync(string tenantId, string userId)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT mfa_secret FROM users WHERE tenant_id = @t AND user_id = @u";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("u", userId);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    /// <summary>
    /// Reads the raw <c>users.mfa_recovery_codes</c> array bypassing the store
    /// entirely, so tests can assert per-element ciphertext and byte-for-byte
    /// stability across an idempotent migrator re-run.
    /// </summary>
    public async Task<string[]?> ReadRawRecoveryCodesAsync(string tenantId, string userId)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT mfa_recovery_codes FROM users WHERE tenant_id = @t AND user_id = @u";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("u", userId);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string[])result;
    }

    /// <summary>
    /// Reads every <c>(user_id, mfa_secret)</c> pair for a tenant in one round
    /// trip. The batch-size test plants more rows than the migrator's batch
    /// holds, and per-row reads would dominate its runtime.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> ReadAllRawMfaSecretsAsync(string tenantId)
    {
        var results = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, mfa_secret FROM users WHERE tenant_id = @t";
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        return results;
    }

    /// <summary>
    /// Inserts (or updates) a <c>users</c> row writing <c>mfa_secret</c> and
    /// <c>mfa_recovery_codes</c> <b>directly</b>, bypassing the store's
    /// protect-on-write path — the only way to set up the legacy unwrapped
    /// condition <c>UserMfaEncryptionMigrator</c> is designed to clean up, and
    /// the mixed wrapped/legacy array a crash mid-migration leaves behind.
    /// </summary>
    public Task WriteRawMfaMaterialAsync(
        string tenantId,
        string userId,
        string? mfaSecret,
        string[]? mfaRecoveryCodes)
        => WriteRawMfaMaterialBatchAsync(tenantId, [
            new RawMfaSeedRow
            {
                UserId = userId,
                MfaSecret = mfaSecret,
                MfaRecoveryCodes = mfaRecoveryCodes,
            }]);

    /// <summary>
    /// Bulk form of <see cref="WriteRawMfaMaterialAsync"/>: plants many rows
    /// with chunked multi-row INSERTs so a &gt;500-row population (the
    /// migrator's batch size) seeds in a handful of round trips instead of one
    /// per row.
    /// </summary>
    public async Task WriteRawMfaMaterialBatchAsync(string tenantId, IReadOnlyList<RawMfaSeedRow> rows)
    {
        if (rows.Count == 0) return;

        await using var conn = await DataSource.OpenConnectionAsync();
        for (var offset = 0; offset < rows.Count; offset += InsertChunkSize)
        {
            var take = Math.Min(InsertChunkSize, rows.Count - offset);
            var sql = new StringBuilder(
                "INSERT INTO users (user_id, tenant_id, email, display_name, role, status, created_at, " +
                "mfa_enabled, mfa_secret, mfa_recovery_codes, email_verified, failed_login_attempts, auth_provider) VALUES ");

            await using var cmd = conn.CreateCommand();
            cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = tenantId });

            for (var i = 0; i < take; i++)
            {
                var row = rows[offset + i];
                if (i > 0) sql.Append(", ");
                sql.Append("(@u").Append(i)
                   .Append(", @t, @e").Append(i)
                   .Append(", @d").Append(i)
                   .Append(", 0, 0, now(), true, @s").Append(i)
                   .Append(", @c").Append(i)
                   .Append(", true, 0, 'local')");

                cmd.Parameters.Add(new NpgsqlParameter($"u{i}", NpgsqlDbType.Text) { Value = row.UserId });
                cmd.Parameters.Add(new NpgsqlParameter($"e{i}", NpgsqlDbType.Text) { Value = $"{row.UserId}@legacy.test" });
                cmd.Parameters.Add(new NpgsqlParameter($"d{i}", NpgsqlDbType.Text) { Value = row.UserId });
                // Both MFA parameters carry an explicit NpgsqlDbType: either can be
                // DBNull.Value and an untyped nullable parameter throws Postgres 42P08.
                cmd.Parameters.Add(new NpgsqlParameter($"s{i}", NpgsqlDbType.Text)
                {
                    Value = (object?)row.MfaSecret ?? DBNull.Value,
                });
                cmd.Parameters.Add(new NpgsqlParameter($"c{i}", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = (object?)row.MfaRecoveryCodes ?? DBNull.Value,
                });
            }

            sql.Append(" ON CONFLICT (tenant_id, user_id) DO UPDATE SET ")
               .Append("mfa_secret = EXCLUDED.mfa_secret, mfa_recovery_codes = EXCLUDED.mfa_recovery_codes");

            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Rows per multi-row INSERT when bulk-planting legacy users.</summary>
    private const int InsertChunkSize = 200;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS tenants (
            tenant_id TEXT PRIMARY KEY
        );

        CREATE TABLE IF NOT EXISTS users (
            user_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
            email TEXT NOT NULL,
            display_name TEXT NOT NULL DEFAULT '',
            role INT NOT NULL DEFAULT 0,
            status INT NOT NULL DEFAULT 0,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at TIMESTAMPTZ,
            created_by TEXT,
            updated_by TEXT,
            password_hash TEXT,
            mfa_enabled BOOLEAN NOT NULL DEFAULT false,
            mfa_secret TEXT,
            mfa_recovery_codes TEXT[],
            mfa_confirmed_at TIMESTAMPTZ,
            email_verified BOOLEAN NOT NULL DEFAULT false,
            failed_login_attempts INT NOT NULL DEFAULT 0,
            locked_until TIMESTAMPTZ,
            password_changed_at TIMESTAMPTZ,
            last_login_at TIMESTAMPTZ,
            auth_provider TEXT NOT NULL DEFAULT 'local',
            external_id TEXT,
            oidc_subject TEXT,
            PRIMARY KEY (tenant_id, user_id)
        );
        """;
}

/// <summary>
/// One planted <c>users</c> row for
/// <see cref="UserMfaEncryptionFixture.WriteRawMfaMaterialBatchAsync"/>. Class
/// based with <c>{ get; init; }</c> per the repo's row-type convention.
/// </summary>
public sealed class RawMfaSeedRow
{
    public required string UserId { get; init; }
    public string? MfaSecret { get; init; }
    public string[]? MfaRecoveryCodes { get; init; }
}
