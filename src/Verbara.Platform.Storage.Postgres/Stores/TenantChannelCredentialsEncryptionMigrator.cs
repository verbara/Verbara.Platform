using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// One-shot, idempotent migrator that wraps every legacy plaintext secret credential value in
/// <c>tenant_channel_configs.credentials</c> with DataProtection on host startup
/// (whatsapp-works-for-real D15), for rows written before <see cref="PostgresTenantChannelConfigStore"/>
/// started protecting on write. Mirrors <see cref="UserMfaEncryptionMigrator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Detection is by trial Unprotect per secret value (<see cref="ChannelCredentialSchemas.IsSecret"/>): a
/// value that round-trips is carried through byte-for-byte, one that throws
/// <see cref="CryptographicException"/> is legacy and re-emitted wrapped. Formats are never sniffed.
/// The table holds at most one row per tenant and channel, so it is read whole.
/// </para>
/// <para>
/// Idempotent: a second run finds every secret wrapped and issues zero <c>UPDATE</c>s. A rewrite is
/// conditional on the row still holding what was read (<c>credentials = @Original::jsonb</c>), so a
/// concurrent administrator save is never overwritten — the next boot converges that row instead.
/// Never blocks host startup: a missing table, a cancelled host, a failing row or any other exception is
/// logged and swallowed. Never logs a credential value or ciphertext — only counts, and the tenant and
/// channel of a row that failed.
/// </para>
/// </remarks>
internal sealed partial class TenantChannelCredentialsEncryptionMigrator : IHostedService
{
    private static readonly ActivitySource Source = new("Verbara.Platform.TenantChannelCredentialsEncryption");

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _protector;
    private readonly ILogger<TenantChannelCredentialsEncryptionMigrator> _logger;

    public TenantChannelCredentialsEncryptionMigrator(
        NpgsqlDataSource dataSource,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<TenantChannelCredentialsEncryptionMigrator> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        // Bound from the store's constant, never a re-typed literal: store and migrator drifting apart would
        // make every wrapped value unreadable.
        _protector = dataProtectionProvider.CreateProtector(PostgresTenantChannelConfigStore.CredentialsProtectorPurpose);
        _logger = logger;
    }

    /// <summary>The counts of one run.</summary>
    internal sealed record RunCounts(int ScannedRows, int RewrittenRows, int AlreadyWrappedRows, int FailedRows);

    /// <summary>The counts of the most recent <see cref="StartAsync"/> (all zero before the first run).</summary>
    internal RunCounts LastRun { get; private set; } = new(0, 0, 0, 0);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity("tenant_channel_credentials.encryption_migration");
        var scanned = 0;
        var rewritten = 0;
        var alreadyWrapped = 0;
        var failed = 0;

        try
        {
            var rows = await _dataSource.QueryListAsync(ScanSql, static _ => { }, ChannelConfigRow.Map, cancellationToken);

            foreach (var row in rows)
            {
                if (cancellationToken.IsCancellationRequested) break;

                scanned++;
                try
                {
                    if (await MigrateRowAsync(row, cancellationToken))
                        rewritten++;
                    else
                        alreadyWrapped++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad row must not abandon the others. The exception is logged without the row's value.
                    failed++;
                    LogRowMigrationFailed(_logger, row.TenantId, row.Channel, ex.GetType().Name);
                }
            }

            activity?.SetTag("scanned_rows", scanned);
            activity?.SetTag("rewritten_rows", rewritten);
            activity?.SetTag("already_wrapped_rows", alreadyWrapped);
            activity?.SetTag("failed_rows", failed);
            LogMigrationCompleted(_logger, scanned, rewritten, alreadyWrapped, failed);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // undefined_table: a fresh database the schema runner has not reached yet.
            LogTableMissing(_logger);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — non-fatal.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block startup; the next deploy retries.
            LogMigrationFailed(_logger, ex);
        }
        finally
        {
            LastRun = new RunCounts(scanned, rewritten, alreadyWrapped, failed);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Wraps the row's legacy secret values and rewrites it. Returns <see langword="true"/> when a write was
    /// issued, <see langword="false"/> when every secret was already wrapped.
    /// </summary>
    private async Task<bool> MigrateRowAsync(ChannelConfigRow row, CancellationToken cancellationToken)
    {
        var channel = (ChannelType)row.Channel;
        // Throws JsonException on anything but a string-to-string object; counted as a failed row.
        var stored = JsonSerializer.Deserialize(row.Credentials, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
                     ?? throw new JsonException("credentials is null");

        var rewrittenValues = new Dictionary<string, string>(stored.Count, StringComparer.Ordinal);
        var changed = false;
        foreach (var (key, value) in stored)
        {
            if (ChannelCredentialSchemas.IsSecret(channel, key) && !string.IsNullOrEmpty(value) && !IsWrapped(value))
            {
                rewrittenValues[key] = _protector.Protect(value);
                changed = true;
            }
            else
            {
                rewrittenValues[key] = value;
            }
        }

        if (!changed) return false;

        var json = JsonSerializer.Serialize(rewrittenValues, PostgresJson.Ctx.IReadOnlyDictionaryStringString);
        await _dataSource.ExecuteAsync(
            RewriteSql,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = row.TenantId });
                p.Add(new NpgsqlParameter("Channel", NpgsqlDbType.Integer) { Value = row.Channel });
                p.Add(new NpgsqlParameter("Credentials", NpgsqlDbType.Text) { Value = json });
                p.Add(new NpgsqlParameter("Original", NpgsqlDbType.Text) { Value = row.Credentials });
            },
            cancellationToken);
        return true;
    }

    private bool IsWrapped(string value)
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

    private const string ScanSql =
        "SELECT tenant_id, channel, credentials::text AS credentials FROM tenant_channel_configs " +
        "ORDER BY tenant_id, channel";

    private const string RewriteSql =
        "UPDATE tenant_channel_configs SET credentials = @Credentials::jsonb " +
        "WHERE tenant_id = @TenantId AND channel = @Channel AND credentials = @Original::jsonb";

    [LoggerMessage(EventId = 4301, Level = LogLevel.Information,
        Message = "Channel credential encryption migration completed: {Scanned} scanned, {Rewritten} rows wrapped, {AlreadyWrapped} already wrapped, {Failed} failed")]
    private static partial void LogMigrationCompleted(ILogger logger, int scanned, int rewritten, int alreadyWrapped, int failed);

    [LoggerMessage(EventId = 4302, Level = LogLevel.Error,
        Message = "Channel credential encryption migration failed; will retry on next deploy")]
    private static partial void LogMigrationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4303, Level = LogLevel.Debug,
        Message = "tenant_channel_configs not present yet — skipping channel credential encryption migration")]
    private static partial void LogTableMissing(ILogger logger);

    // The exception object is deliberately not attached: a JSON or crypto exception message can quote the value.
    [LoggerMessage(EventId = 4304, Level = LogLevel.Warning,
        Message = "Channel credential encryption migration failed for one row ({ErrorType}); continuing with the remaining rows (tenant {TenantId}, channel {Channel})")]
    private static partial void LogRowMigrationFailed(ILogger logger, string tenantId, int channel, string errorType);

    private sealed class ChannelConfigRow
    {
        public string TenantId { get; init; } = null!;
        public int Channel { get; init; }
        public string Credentials { get; init; } = null!;

        public static ChannelConfigRow Map(NpgsqlDataReader r) => new()
        {
            TenantId = r.GetString("tenant_id"),
            Channel = r.GetInt32("channel"),
            Credentials = r.GetString("credentials"),
        };
    }
}
