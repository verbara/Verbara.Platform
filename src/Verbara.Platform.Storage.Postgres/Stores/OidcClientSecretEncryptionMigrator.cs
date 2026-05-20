using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// One-shot, idempotent migrator that wraps any legacy plaintext
/// <c>oidc_client_secret</c> rows in <c>tenant_auth_config</c> with
/// DataProtection on host startup. ADMIN-001 (PREPUB-2026-05-09) remediation.
/// </summary>
/// <remarks>
/// <para>
/// Runs once per process boot from <see cref="StartAsync"/>. Detection of
/// "already encrypted" is by trial Unprotect: if the value round-trips
/// through <see cref="IDataProtector"/>, it is left alone; if the call
/// throws <see cref="CryptographicException"/>, the value is treated as
/// legacy plaintext and re-saved through <see cref="IDataProtector"/>.
/// </para>
/// <para>
/// Idempotent: a second run finds every row already encrypted and
/// performs zero writes. Safe to leave registered indefinitely.
/// </para>
/// <para>
/// The migrator never logs the secret value itself, only counts of rows
/// scanned / re-encrypted, plus an Activity for telemetry pipelines.
/// </para>
/// </remarks>
internal sealed partial class OidcClientSecretEncryptionMigrator : IHostedService
{
    private static readonly ActivitySource Source = new("Verbara.Platform.OidcClientSecretEncryption");

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _protector;
    private readonly ILogger<OidcClientSecretEncryptionMigrator> _logger;

    public OidcClientSecretEncryptionMigrator(
        NpgsqlDataSource dataSource,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<OidcClientSecretEncryptionMigrator> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _protector = dataProtectionProvider.CreateProtector(
            PostgresTenantAuthConfigStore.OidcClientSecretProtectorPurpose);
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity("oidc_secret.encryption_migration");
        var encryptedCount = 0;
        var alreadyEncryptedCount = 0;

        try
        {
            // 024_EncryptOidcClientSecret.sql is a no-op marker — the table itself
            // has existed since 001 and the column since the OIDC feature shipped.
            // If the table is absent (fresh install), this query returns no rows
            // and the migrator is a no-op.
            var rows = await _dataSource.QueryListAsync(
                "SELECT tenant_id, oidc_client_secret FROM tenant_auth_config WHERE oidc_client_secret IS NOT NULL",
                static p => { },
                OidcSecretRow.Map, cancellationToken);

            foreach (var row in rows)
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (TryUnprotect(row.Value))
                {
                    alreadyEncryptedCount++;
                    continue;
                }

                var encrypted = _protector.Protect(row.Value);
                await _dataSource.ExecuteAsync(
                    "UPDATE tenant_auth_config SET oidc_client_secret = @Value WHERE tenant_id = @TenantId",
                    p =>
                    {
                        p.Add(new NpgsqlParameter("TenantId", row.TenantId));
                        p.Add(new NpgsqlParameter("Value", encrypted));
                    },
                    cancellationToken);
                encryptedCount++;
            }

            activity?.SetTag("encrypted_rows", encryptedCount);
            activity?.SetTag("already_encrypted_rows", alreadyEncryptedCount);
            LogMigrationCompleted(_logger, rows.Count, encryptedCount, alreadyEncryptedCount);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            // 42P01 = undefined_table. tenant_auth_config absent on a fresh
            // install where DatabaseMigrationService has not yet applied the
            // initial schema; the migrator runs after the migration runner in
            // production but the host startup ordering is best-effort. Treat
            // missing-table as a no-op so test fixtures and bare boot orders
            // stay quiet.
            LogTableMissing(_logger);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down before we finished — non-fatal.
        }
        catch (Exception ex)
        {
            // Migration failure must NOT block host startup; the next deploy
            // will retry. Log loud and move on.
            LogMigrationFailed(_logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool TryUnprotect(string value)
    {
        try
        {
            _protector.Unprotect(value);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information,
        Message = "OIDC client secret encryption migration completed: {Total} scanned, {Encrypted} legacy plaintext re-encrypted, {AlreadyEncrypted} already encrypted")]
    private static partial void LogMigrationCompleted(ILogger logger, int total, int encrypted, int alreadyEncrypted);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Error,
        Message = "OIDC client secret encryption migration failed; will retry on next deploy")]
    private static partial void LogMigrationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Debug,
        Message = "tenant_auth_config not present yet — skipping OIDC secret migration")]
    private static partial void LogTableMissing(ILogger logger);

    private sealed class OidcSecretRow
    {
        public string TenantId { get; init; } = null!;
        public string Value { get; init; } = null!;

        public static OidcSecretRow Map(NpgsqlDataReader r) => new()
        {
            TenantId = r.GetString("tenant_id"),
            Value = r.GetString("oidc_client_secret"),
        };
    }
}
