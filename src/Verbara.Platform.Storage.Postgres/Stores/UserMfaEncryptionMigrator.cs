using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// One-shot, idempotent migrator that wraps any legacy unwrapped
/// <c>mfa_secret</c> / <c>mfa_recovery_codes</c> values in <c>users</c> with
/// DataProtection on host startup. Closes threat-model asset <b>A7</b>
/// (MFA enrollment material) for rows written before the store started
/// protecting on write, mirroring
/// <see cref="OidcClientSecretEncryptionMigrator"/> (ADMIN-001).
/// </summary>
/// <remarks>
/// <para>
/// Runs once per process boot from <see cref="StartAsync"/>. Detection of
/// "already wrapped" is by trial Unprotect: if the value round-trips through
/// <see cref="IDataProtector"/>, it is carried through byte-for-byte; if the
/// call throws <see cref="CryptographicException"/>, the value is treated as
/// legacy and re-emitted wrapped. The verdict is reached per value and, for
/// the <c>TEXT[]</c> column, per element — so a row left half-rewritten by a
/// crash converges on the next boot. Formats are never sniffed: recovery-code
/// elements are opaque strings and two hash formats coexist in that column.
/// </para>
/// <para>
/// Unlike <c>tenant_auth_config</c>, which holds one row per tenant and can be
/// materialised whole, <c>users</c> is unbounded — so this migrator walks it in
/// keyset-ordered batches (<see cref="BatchSize"/> rows, cursor over
/// <c>(tenant_id, user_id)</c>) rather than loading every matching row at once.
/// </para>
/// <para>
/// Idempotent: a second run finds every value already wrapped and issues zero
/// <c>UPDATE</c>s — a fully wrapped row is never rewritten, so an idempotent
/// re-run costs no ciphertext churn and no WAL. Safe to leave registered
/// indefinitely. Never blocks host startup: a missing table, a cancelled host,
/// a failing row, or any other exception is logged and swallowed, and the next
/// deploy retries.
/// </para>
/// <para>
/// The migrator never logs an MFA secret, a recovery-code value, a ciphertext,
/// or a user email — only counts of rows scanned / newly wrapped / already
/// wrapped / failed, plus the tenant and user identifiers of a row that failed,
/// and an Activity carrying the same counts for telemetry pipelines.
/// </para>
/// </remarks>
internal sealed partial class UserMfaEncryptionMigrator : IHostedService
{
    private static readonly ActivitySource Source = new("Verbara.Platform.UserMfaEncryption");

    /// <summary>Rows per keyset batch. Bounds the migrator's memory footprint on a large <c>users</c> table.</summary>
    private const int BatchSize = 500;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _secretProtector;
    private readonly IDataProtector _recoveryCodesProtector;
    private readonly ILogger<UserMfaEncryptionMigrator> _logger;

    public UserMfaEncryptionMigrator(
        NpgsqlDataSource dataSource,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<UserMfaEncryptionMigrator> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        // Bound from the store's constants, never from re-typed literals: the
        // purpose string is a persistence contract and store/migrator drifting
        // apart would make every wrapped value unreadable.
        _secretProtector = dataProtectionProvider.CreateProtector(
            PostgresUserStore.MfaSecretProtectorPurpose);
        _recoveryCodesProtector = dataProtectionProvider.CreateProtector(
            PostgresUserStore.MfaRecoveryCodesProtectorPurpose);
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity("user_mfa.encryption_migration");
        var scannedCount = 0;
        var encryptedCount = 0;
        var alreadyEncryptedCount = 0;
        var failedCount = 0;

        try
        {
            // Keyset cursor, seeded with the empty-string sentinel: tenant_id and
            // user_id are both non-null TEXT, so ('','') sorts before every real
            // key and the first batch matches every row. LIMIT/OFFSET is not an
            // option here — the migrator mutates rows inside the predicate's own
            // result set, and offset paging can skip rows when the plan reorders.
            var lastTenantId = string.Empty;
            var lastUserId = string.Empty;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var cursorTenantId = lastTenantId;
                var cursorUserId = lastUserId;

                var rows = await _dataSource.QueryListAsync(
                    ScanSql,
                    p =>
                    {
                        p.Add(new NpgsqlParameter("LastTenantId", NpgsqlDbType.Text) { Value = cursorTenantId });
                        p.Add(new NpgsqlParameter("LastUserId", NpgsqlDbType.Text) { Value = cursorUserId });
                        p.Add(new NpgsqlParameter("BatchSize", NpgsqlDbType.Integer) { Value = BatchSize });
                    },
                    UserMfaRow.Map, cancellationToken);

                if (rows.Count == 0) break;

                foreach (var row in rows)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    scannedCount++;
                    try
                    {
                        if (await MigrateRowAsync(row, cancellationToken))
                        {
                            encryptedCount++;
                        }
                        else
                        {
                            alreadyEncryptedCount++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Host shutdown — hand it to the outer handler, do not
                        // record it as a row failure.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // One bad row must not abandon the rest of the batch or
                        // the remaining batches. Count it and keep walking.
                        failedCount++;
                        LogRowMigrationFailed(_logger, row.TenantId, row.UserId, ex);
                    }
                }

                // Advance the cursor to the last row of the batch. The UPDATE never
                // touches the key columns, so the ordering stays stable across the walk.
                lastTenantId = rows[^1].TenantId;
                lastUserId = rows[^1].UserId;

                if (rows.Count < BatchSize) break;
            }

            activity?.SetTag("scanned_rows", scannedCount);
            activity?.SetTag("encrypted_rows", encryptedCount);
            activity?.SetTag("already_encrypted_rows", alreadyEncryptedCount);
            activity?.SetTag("failed_rows", failedCount);
            LogMigrationCompleted(_logger, scannedCount, encryptedCount, alreadyEncryptedCount, failedCount);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // 42P01 = undefined_table. `users` absent on a fresh install where
            // DatabaseMigrationService has not yet applied the initial schema;
            // the migrator runs after the schema runner in production but the
            // host startup ordering is best-effort. Treat missing-table as a
            // no-op so test fixtures and bare boot orders stay quiet.
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

    /// <summary>
    /// Wraps whatever is legacy in <paramref name="row"/> and rewrites it.
    /// Returns <see langword="true"/> when the row carried at least one legacy
    /// value and was rewritten, <see langword="false"/> when every value was
    /// already wrapped and no write was issued.
    /// </summary>
    private async Task<bool> MigrateRowAsync(UserMfaRow row, CancellationToken cancellationToken)
    {
        var rowChanged = false;

        var secret = row.MfaSecret;
        // Null/empty keeps its column shape — there is nothing to wrap, and
        // wrapping an empty string would turn SQL "no secret" into ciphertext.
        if (!string.IsNullOrEmpty(secret) && !TryUnprotect(_secretProtector, secret))
        {
            secret = _secretProtector.Protect(secret);
            rowChanged = true;
        }

        var codes = row.MfaRecoveryCodes;
        if (codes is { Length: > 0 })
        {
            var rewritten = new string[codes.Length];
            for (var i = 0; i < codes.Length; i++)
            {
                var element = codes[i];
                // Per element: a round-trip means already wrapped (carried through
                // byte-for-byte), a CryptographicException means legacy. Never a
                // format sniff — the elements are opaque strings and BCrypt and
                // SHA-256-hex digests coexist in this column.
                if (!string.IsNullOrEmpty(element) && !TryUnprotect(_recoveryCodesProtector, element))
                {
                    rewritten[i] = _recoveryCodesProtector.Protect(element);
                    rowChanged = true;
                }
                else
                {
                    rewritten[i] = element;
                }
            }

            codes = rewritten;
        }

        if (!rowChanged) return false;

        await _dataSource.ExecuteAsync(
            RewriteSql,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = row.TenantId });
                p.Add(new NpgsqlParameter("UserId", NpgsqlDbType.Text) { Value = row.UserId });
                // Both value parameters are explicitly typed: either can be
                // DBNull.Value and an untyped nullable parameter throws 42P08.
                p.Add(new NpgsqlParameter("MfaSecret", NpgsqlDbType.Text) { Value = (object?)secret ?? DBNull.Value });
                p.Add(new NpgsqlParameter("MfaRecoveryCodes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (object?)codes ?? DBNull.Value });
            },
            cancellationToken);

        return true;
    }

    private static bool TryUnprotect(IDataProtector protector, string value)
    {
        try
        {
            protector.Unprotect(value);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private const string ScanSql =
        "SELECT tenant_id, user_id, mfa_secret, mfa_recovery_codes " +
        "FROM users " +
        "WHERE (mfa_secret IS NOT NULL OR mfa_recovery_codes IS NOT NULL) " +
        "  AND (tenant_id, user_id) > (@LastTenantId, @LastUserId) " +
        "ORDER BY tenant_id, user_id " +
        "LIMIT @BatchSize";

    private const string RewriteSql =
        "UPDATE users SET mfa_secret = @MfaSecret, mfa_recovery_codes = @MfaRecoveryCodes " +
        "WHERE tenant_id = @TenantId AND user_id = @UserId";

    [LoggerMessage(EventId = 4201, Level = LogLevel.Information,
        Message = "User MFA encryption migration completed: {Scanned} scanned, {Encrypted} legacy rows wrapped, {AlreadyEncrypted} already wrapped, {Failed} failed")]
    private static partial void LogMigrationCompleted(ILogger logger, int scanned, int encrypted, int alreadyEncrypted, int failed);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Error,
        Message = "User MFA encryption migration failed; will retry on next deploy")]
    private static partial void LogMigrationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Debug,
        Message = "users not present yet — skipping user MFA encryption migration")]
    private static partial void LogTableMissing(ILogger logger);

    [LoggerMessage(EventId = 4204, Level = LogLevel.Warning,
        Message = "User MFA encryption migration failed for one row; continuing with the remaining rows (tenant {TenantId}, user {UserId})")]
    private static partial void LogRowMigrationFailed(ILogger logger, string tenantId, string userId, Exception ex);

    private sealed class UserMfaRow
    {
        public string TenantId { get; init; } = null!;
        public string UserId { get; init; } = null!;
        public string? MfaSecret { get; init; }
        public string[]? MfaRecoveryCodes { get; init; }

        public static UserMfaRow Map(NpgsqlDataReader r) => new()
        {
            TenantId = r.GetString("tenant_id"),
            UserId = r.GetString("user_id"),
            MfaSecret = r.GetStringOrNull("mfa_secret"),
            MfaRecoveryCodes = r.IsDBNull(r.GetOrdinal("mfa_recovery_codes"))
                ? null
                : r.GetFieldValue<string[]>(r.GetOrdinal("mfa_recovery_codes")),
        };
    }
}
