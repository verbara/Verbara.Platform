using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// AHH Phase 3.B — one-shot migration of the legacy file-based JWT signing
/// key into the rotation pool. Runs once at startup before any token is
/// issued: if the pool is empty AND <c>jwt-signing-key.xml</c> exists in
/// the configured data directory, the legacy RSA key is imported as an
/// active <c>RS256</c> entry so already-issued tokens continue to validate
/// during the cutover window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent</b>: subsequent boots find an active entry already in the
/// pool and do nothing. Multi-replica boot is safe — each replica races
/// to import; the first writer wins (the others observe a non-empty pool
/// when they re-check). The race wastes at most one redundant
/// <see cref="IJwtKeyStore.UpsertAsync"/> call; functionally both replicas
/// converge on the same key.
/// </para>
/// <para>
/// <b>Failure modes</b>:
/// </para>
/// <list type="bullet">
///   <item>
///     <description><c>jwt-signing-key.xml</c> missing → no-op. The pool
///     auto-bootstraps a fresh <c>HS256</c> entry on first
///     <see cref="IJwtKeyRotationService.GetActiveSigningKeyAsync"/>; only
///     pre-existing tokens are affected (validation 401 → user re-logs).
///     This path is normal for greenfield deploys.</description>
///   </item>
///   <item>
///     <description>File present but unreadable / unreasonably-formatted →
///     logged at error level + skipped. The pool auto-bootstraps as above.
///     Operators inspect the log to recover the file (rare; only happens
///     on disk corruption or DataProtection key loss).</description>
///   </item>
///   <item>
///     <description><c>IJwtKeyStore.UpsertAsync</c> fails (e.g. Redis down
///     at boot) → exception bubbles to the host shutdown path, blocking
///     start. Same posture as any storage-layer outage: fail-fast is
///     correct since auth would be broken anyway.</description>
///   </item>
/// </list>
/// </remarks>
internal sealed partial class JwtLegacyKeyMigrationService : IHostedService
{
    private readonly IJwtKeyStore _store;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly string _legacyDataDirectory;
    private readonly ILogger<JwtLegacyKeyMigrationService> _logger;

    public JwtLegacyKeyMigrationService(
        IJwtKeyStore store,
        IDataProtectionProvider dataProtection,
        string legacyDataDirectory,
        ILogger<JwtLegacyKeyMigrationService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dataProtection);
        ArgumentNullException.ThrowIfNull(legacyDataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _dataProtection = dataProtection;
        _legacyDataDirectory = legacyDataDirectory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await TryImportLegacyKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or InvalidOperationException)
        {
            // Non-fatal: pool auto-bootstrap will produce a fresh HS256 entry.
            // Existing tokens fail validation gracefully (401 → re-login).
            LogMigrationFailedNonFatal(_logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task TryImportLegacyKeyAsync(CancellationToken ct)
    {
        // Step 1: skip if pool already has an active entry (idempotent).
        var existingActive = await _store.GetActiveAsync(ct).ConfigureAwait(false);
        if (existingActive is not null)
        {
            LogPoolAlreadySeeded(_logger, existingActive.KeyId);
            return;
        }

        // Step 2: skip if no legacy file (greenfield deploy → auto-bootstrap).
        var keyPath = JwtTokenService.LegacyKeyFilePath(_legacyDataDirectory);
        if (!File.Exists(keyPath))
        {
            LogNoLegacyFile(_logger, keyPath);
            return;
        }

        // Step 3: decrypt + parse the legacy XML.
        var raw = await File.ReadAllBytesAsync(keyPath, ct).ConfigureAwait(false);
        var protector = _dataProtection.CreateProtector(JwtTokenService.DataProtectorPurposeForMigration());

        string xmlString;
        try
        {
            xmlString = Encoding.UTF8.GetString(protector.Unprotect(raw));
        }
        catch (CryptographicException)
        {
            // Plaintext-XML legacy fallback — matches the file-based constructor's
            // tolerance for keys written before DataProtection was introduced.
            xmlString = Encoding.UTF8.GetString(raw);
        }

        using var rsa = RSA.Create();
        rsa.FromXmlString(xmlString);

        // Step 4: build the JwtKeyEntry + write it as the active key.
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var kid = JwtTokenService.DeriveKeyIdFromRsaInternal(rsa);
        var now = DateTimeOffset.UtcNow;
        var migrated = new JwtKeyEntry
        {
            KeyId = kid,
            Key = Convert.ToBase64String(pkcs8),
            Algorithm = JwtKeyAlgorithm.Rs256,
            ActivatedAt = now,
            // Generous 30-day window so already-issued tokens survive any
            // subsequent rotation cadence. Operators triggering an explicit
            // rotation via /management/security/jwt/rotate-key demote this
            // entry naturally.
            ExpiresAt = now.AddDays(30),
            IsActive = true,
        };

        await _store.UpsertAsync(migrated, ct).ConfigureAwait(false);
        LogMigrationCompleted(_logger, kid);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "JWT pool already has active key '{KeyId}'; legacy migration skipped.")]
    private static partial void LogPoolAlreadySeeded(ILogger logger, string keyId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "No legacy jwt-signing-key.xml at '{Path}'; pool will auto-bootstrap on first issuance.")]
    private static partial void LogNoLegacyFile(ILogger logger, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Legacy JWT key imported into rotation pool as active RS256 entry kid='{KeyId}'.")]
    private static partial void LogMigrationCompleted(ILogger logger, string keyId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Legacy JWT key migration failed (non-fatal). Pool will auto-bootstrap a fresh HS256 entry; pre-existing tokens will fail validation.")]
    private static partial void LogMigrationFailedNonFatal(ILogger logger, Exception ex);
}
