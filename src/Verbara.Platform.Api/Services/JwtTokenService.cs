using System.Diagnostics.Metrics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Issues Platform JWTs, and holds the <see cref="ValidationParameters"/> the JwtBearer handler
/// validates them with.
/// </summary>
/// <remarks>
/// <para>
/// Two construction paths coexist:
/// </para>
/// <list type="bullet">
///   <item>
///     <description><b>File-based RSA</b> (the original constructor) — loads
///     a single RSA-2048 private key from
///     <c>{dataDirectory}/jwt-signing-key.xml</c>, encrypted at rest via
///     DataProtection. Single-process safe; used by tests and any deployment
///     that has not opted into the rotation pool yet.</description>
///   </item>
///   <item>
///     <description><b>Rotation pool</b> (AHH Phase 3.B) — consumes
///     <see cref="IJwtKeyRotationService"/>. The active signing entry is
///     cached for 5 minutes and refreshed in the background, so issuance waits
///     on the pool only for its first key. Validation reads an in-memory
///     <see cref="JwtValidationKeySnapshot"/> holding every pool key, so tokens
///     issued under prior keys still verify during the grace window and a token
///     check never waits on the pool.
///     A migration shim imports the legacy file as an <c>RS256</c> entry on
///     first boot when the pool is empty + the file exists.</description>
///   </item>
/// </list>
/// <para>
/// Both paths produce externally identical behavior — same claims, same
/// <c>kid</c> header convention, same validation parameters — so endpoints
/// don't care which path is wired.
/// </para>
/// <para>
/// Revocation is not this class's concern: the JwtBearer <c>OnTokenValidated</c>
/// event refuses a revoked impersonation token on every request
/// (<see cref="Verbara.Platform.Identity.Auth.ImpersonationTokenRevocation"/>).
/// </para>
/// </remarks>
internal sealed partial class JwtTokenService
{
    private const string Issuer = "verbara-platform";
    private const string Audience = "verbara-platform";
    private const string DataProtectorPurpose = "Verbara.Platform.Jwt.SigningKey";
    private const string LegacyKeyFileName = "jwt-signing-key.xml";
    private const string FileKeyIdPrefix = "platform-jwt-";

    // Observability — R5.5 Phase C-LK Tier-1 hardening. See
    // docs/research/2026-05-24-jti-investigation-presence-vu1500.md §
    // "Validation experiment" for why these counters exist. Counter names
    // follow the OpenTelemetry semantic-conventions style used elsewhere in
    // Verbara.Platform.Api (AuthWriteQueue, ResilienceStateObserver).
    private const string MeterName = "verbara.platform.jwt";

    /// <summary>
    /// How long an access token authenticates. Also the longest an API-key authentication is held
    /// to (<see cref="ApiKeyAuthenticationHandler"/>), so a live connection opened with either is
    /// re-authenticated at least this often.
    /// </summary>
    internal static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ImpersonationTokenLifetime = TimeSpan.FromMinutes(30);

    // 5 min — large enough to amortize Redis SCAN+N×GET cost across HPA scale
    // events, small enough that a freshly-rotated key propagates within minutes.
    // Bumped from 60s after R5.5 Phase C-LK observed HPA-induced cold-cache
    // 401 cascades. The key-rotation cadence is 24h ActiveDuration + 24h
    // GracePeriod (48h total validation window), so 5 min cache is still ~570×
    // safer than the rotation cadence. See docs/research/2026-05-24-jti-
    // investigation-presence-vu1500.md § "Recommended fix path Tier 1".
    private static readonly TimeSpan ActiveKeyCacheTtl = TimeSpan.FromMinutes(5);

    // After a failed signing-key refresh with a cached key still in hand, wait this long before
    // trying again rather than starting one per issuance.
    private static readonly TimeSpan SigningRetryAfterFailure = TimeSpan.FromSeconds(5);

    // ─── File-based path (R5.4 + earlier; tests; single-process bootstrap) ──
    private readonly RsaSecurityKey? _fileSigningKey;
    private readonly SigningCredentials? _fileSigningCredentials;
    private readonly TokenValidationParameters? _fileValidationParameters;

    // ─── Rotation-pool path (AHH Phase 3.B; production multi-replica) ───────
    private readonly IJwtKeyRotationService? _rotationService;
    private readonly TokenValidationParameters? _poolValidationParameters;
    private readonly JwtValidationKeySnapshot? _validationKeys;
    private readonly TimeProvider _time;
    private CachedActive? _cachedActive;
    private Task<Exception?>? _signingRefresh;
    private long _signingRetryAtTicks;

    // ─── Observability (R5.5 Phase C-LK Tier-1 hardening) ──────────────────
    private readonly ILogger<JwtTokenService> _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _cacheMisses;
    private readonly Counter<long> _staleCacheFallbacks;
    private readonly Counter<long> _failClosedThrows;

    // ─── File-based constructor (existing) ──────────────────────────────────

    public JwtTokenService(
        string dataDirectory,
        IDataProtectionProvider dataProtection,
        ILogger<JwtTokenService>? logger = null,
        IMeterFactory? meterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(dataProtection);

        _time = TimeProvider.System;
        (_logger, _meter, _cacheMisses, _staleCacheFallbacks, _failClosedThrows) = InitObservability(logger, meterFactory);

        var rsa = LoadOrCreateFileBasedKey(dataDirectory, dataProtection);
        var kid = DeriveKeyIdFromRsa(rsa);

        _fileSigningKey = new RsaSecurityKey(rsa) { KeyId = kid };
        _fileSigningCredentials = new SigningCredentials(_fileSigningKey, SecurityAlgorithms.RsaSha256);
        _fileValidationParameters = BuildSingleKeyValidationParameters(_fileSigningKey);
    }

    // ─── Rotation-pool constructor (AHH Phase 3.B) ──────────────────────────

    /// <summary>
    /// Construct a JWT service backed by the multi-replica rotation pool.
    /// The pool is expected to already contain at least one active entry
    /// when this service first issues a token; if it is empty,
    /// <see cref="IJwtKeyRotationService.GetActiveSigningKeyAsync"/> auto-
    /// rotates and produces a fresh <c>HS256</c> entry. To preserve
    /// validity of already-issued tokens during the cutover from the
    /// file-based key, register
    /// <see cref="JwtLegacyKeyMigrationService"/> as a hosted service so
    /// the legacy file is imported into the pool as an <c>RS256</c> entry
    /// before any request fires.
    /// </summary>
    public JwtTokenService(
        IJwtKeyRotationService rotationService,
        ILogger<JwtTokenService>? logger = null,
        IMeterFactory? meterFactory = null,
        TimeProvider? timeProvider = null,
        JwtValidationKeySnapshotOptions? validationKeyOptions = null)
    {
        ArgumentNullException.ThrowIfNull(rotationService);

        _rotationService = rotationService;
        _time = timeProvider ?? TimeProvider.System;
        (_logger, _meter, _cacheMisses, _staleCacheFallbacks, _failClosedThrows) = InitObservability(logger, meterFactory);

        _validationKeys = new JwtValidationKeySnapshot(
            ct => rotationService.GetValidationKeysAsync(ct),
            entry => BuildSigningCredentials(entry).Key,
            (validationKeyOptions ?? new JwtValidationKeySnapshotOptions()) with { RefreshInterval = ActiveKeyCacheTtl },
            _time,
            new JwtValidationKeySnapshotEvents
            {
                ReloadStarted = () =>
                {
                    _cacheMisses.Add(1, new KeyValuePair<string, object?>("path", "validation"));
                    LogValidationKeyCacheMiss(_logger);
                },
                ReloadFailed = (ex, stillServing) =>
                {
                    if (stillServing)
                    {
                        _staleCacheFallbacks.Add(1, new KeyValuePair<string, object?>("path", "validation"));
                        LogStaleCacheFallback(_logger, "validation", ex);
                    }
                    else
                    {
                        _failClosedThrows.Add(1, new KeyValuePair<string, object?>("path", "validation"));
                        LogFailClosedNoCacheYet(_logger, "validation", ex);
                    }
                },
            });

        // The resolver runs inside the token handler, synchronously, on every request: it reads the
        // in-memory snapshot only. Loads run in the background (EnsureValidationKeysAsync awaits the
        // first one from the JwtBearer OnMessageReceived event).
        var validationKeys = _validationKeys;
        _poolValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, kid, _) => validationKeys.Resolve(kid),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role",
            NameClaimType = "sub",
        };
    }

    // ─── Public surface (uniform across both paths) ─────────────────────────

    public TokenValidationParameters ValidationParameters =>
        _rotationService is not null ? _poolValidationParameters! : _fileValidationParameters!;

    /// <summary>
    /// The key id tokens are signed with: the file key's, or the cached pool key's (<see langword="null"/>
    /// before this replica has signed with the pool). Never reads the pool.
    /// </summary>
    public string? KeyId => _rotationService is null
        ? _fileSigningKey!.KeyId
        : Volatile.Read(ref _cachedActive)?.Credentials.Key.KeyId;

    /// <summary>
    /// Awaits the validation keys' first load; afterwards starts a background refresh when one is due.
    /// Called from the JwtBearer <c>OnMessageReceived</c> event, before the token is checked. A no-op
    /// on the file-key path.
    /// </summary>
    public Task EnsureValidationKeysAsync(CancellationToken cancellationToken) =>
        EnsureValidationKeysAsync(null, cancellationToken);

    /// <summary>
    /// <see cref="EnsureValidationKeysAsync(CancellationToken)"/> for a token naming
    /// <paramref name="keyId"/>: when the validation keys do not hold it, also awaits one rate-limited
    /// reload (<see cref="JwtValidationKeySnapshot.EnsureLoadedAsync(string?, CancellationToken)"/>).
    /// </summary>
    public Task EnsureValidationKeysAsync(string? keyId, CancellationToken cancellationToken) =>
        _validationKeys?.EnsureLoadedAsync(keyId, cancellationToken) ?? Task.CompletedTask;

    /// <summary>The validation-key reload running now, or a completed task. For tests.</summary>
    internal Task ValidationKeyReloadInFlight => _validationKeys?.ReloadInFlight ?? Task.CompletedTask;

    /// <summary>The signing-key refresh running now, or a completed task. For tests.</summary>
    internal Task SigningKeyRefreshInFlight => Volatile.Read(ref _signingRefresh) ?? Task.CompletedTask;

    public Task<(string Token, DateTimeOffset ExpiresAt)> GenerateAccessTokenAsync(
        User user, CancellationToken cancellationToken = default)
        => GenerateAccessTokenAsync(user, null, cancellationToken);

    public async Task<(string Token, DateTimeOffset ExpiresAt)> GenerateAccessTokenAsync(
        User user, IReadOnlySet<string>? permissions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.Value),
            new("tid", user.TenantId.Value),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (permissions is { Count: > 0 })
        {
            foreach (var permission in permissions)
            {
                claims.Add(new Claim("permissions", permission));
            }
        }

        return await SignTokenAsync(claims, now, expiresAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mints an impersonation token: Admin in <paramref name="targetTenantId"/> on behalf of
    /// <paramref name="admin"/>, for 30 minutes. It records <paramref name="admin"/>'s role, to which
    /// every request that presents it is held (<see cref="AccountStatusGate.ImpersonatorMayAuthenticateAsync"/>).
    /// </summary>
    /// <returns>
    /// The token, its expiry, and its <c>jti</c> (<c>TokenId</c>) — the handle its session keeps so
    /// that revoking or timing out the session can revoke the token.
    /// </returns>
    public async Task<(string Token, DateTimeOffset ExpiresAt, string TokenId)> GenerateImpersonationTokenAsync(
        User admin, string targetTenantId, IReadOnlySet<string> targetPermissions, bool readOnly = false,
        string? impersonationSessionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(targetTenantId);
        ArgumentNullException.ThrowIfNull(targetPermissions);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(ImpersonationTokenLifetime);
        var tokenId = Guid.NewGuid().ToString();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, admin.UserId.Value),
            new("tid", targetTenantId),
            new(JwtRegisteredClaimNames.Email, admin.Email),
            new("name", admin.DisplayName),
            new(ClaimTypes.Role, "Admin"),
            new("impersonator_id", admin.UserId.Value),
            new("impersonator_tenant", admin.TenantId.Value),
            new(AccountStatusGate.ImpersonatorRoleClaim, admin.Role.ToString()),
            new("impersonation", "true"),
            new(JwtRegisteredClaimNames.Jti, tokenId),
        };

        // R5.2 PB.2 — when supplied, link the JWT to its admin-visible session
        // record so EndImpersonation can locate the session.
        if (!string.IsNullOrEmpty(impersonationSessionId))
            claims.Add(new Claim("impersonation_session_id", impersonationSessionId));

        if (readOnly)
            claims.Add(new Claim("readonly", "true"));

        foreach (var permission in targetPermissions)
        {
            claims.Add(new Claim("permissions", permission));
        }

        var (token, signedExpiresAt) = await SignTokenAsync(claims, now, expiresAt, cancellationToken).ConfigureAwait(false);
        return (token, signedExpiresAt, tokenId);
    }

    // ─── Private — token signing dispatch ───────────────────────────────────

    private async Task<(string Token, DateTimeOffset ExpiresAt)> SignTokenAsync(
        List<Claim> claims, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var credentials = await GetActiveSigningCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = credentials,
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateEncodedJwt(descriptor);
        return (token, expiresAt);
    }

    /// <summary>
    /// The credentials to sign with. A cached pool key is returned at once; when it is older than
    /// <see cref="ActiveKeyCacheTtl"/> a background refresh replaces it. Only the first issuance on a
    /// replica awaits the pool (asynchronously), and it fails closed when that read fails.
    /// </summary>
    private async ValueTask<SigningCredentials> GetActiveSigningCredentialsAsync(CancellationToken cancellationToken)
    {
        if (_rotationService is null)
            return _fileSigningCredentials!;

        var cached = Volatile.Read(ref _cachedActive);
        if (cached is not null)
        {
            var nowTicks = _time.GetUtcNow().UtcTicks;
            if (nowTicks - cached.CachedAt.UtcTicks >= ActiveKeyCacheTtl.Ticks
                && nowTicks >= Interlocked.Read(ref _signingRetryAtTicks))
            {
                _ = StartSigningRefresh();
            }
            return cached.Credentials;
        }

        var failure = await StartSigningRefresh().WaitAsync(cancellationToken).ConfigureAwait(false);
        cached = Volatile.Read(ref _cachedActive);
        if (cached is not null)
            return cached.Credentials;

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(
            failure ?? new InvalidOperationException("No JWT signing key could be loaded."));
        return null!; // unreachable
    }

    /// <summary>Starts a signing-key refresh unless one is running; its result is the failure, if any.</summary>
    private Task<Exception?> StartSigningRefresh()
    {
        var running = Volatile.Read(ref _signingRefresh);
        if (running is not null)
            return running;

        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        running = Interlocked.CompareExchange(ref _signingRefresh, done.Task, null);
        if (running is not null)
            return running;

        _ = Task.Run(() => RefreshSigningKeyAsync(done));
        return done.Task;
    }

    private async Task RefreshSigningKeyAsync(TaskCompletionSource<Exception?> done)
    {
        Exception? failure = null;
        var previous = Volatile.Read(ref _cachedActive);
        try
        {
            _cacheMisses.Add(1, new KeyValuePair<string, object?>("path", "signing"));
            LogActiveKeyCacheMiss(_logger);

            var entry = await _rotationService!.GetActiveSigningKeyAsync().ConfigureAwait(false);
            var credentials = BuildSigningCredentials(entry);
            // Validation must accept what this replica now signs with: add the key to the snapshot
            // (never clear it) before any token signed with it leaves.
            _validationKeys!.Add(entry);
            Volatile.Write(ref _cachedActive, new CachedActive(entry, credentials, _time.GetUtcNow()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reported to the awaiting caller (first issuance) or absorbed by the stale key.
            failure = ex;
            Interlocked.Exchange(ref _signingRetryAtTicks, _time.GetUtcNow().Add(SigningRetryAfterFailure).UtcTicks);
            if (previous is not null)
            {
                // Stale-cache fallback (R5.5 Phase C-LK Tier-1 hardening): a Redis blip or
                // transient timeout keeps signing with the previously-cached active key (its
                // validation entry is still within the 48h rotation window, and every validator
                // still accepts it because validation holds the full grace pool).
                _staleCacheFallbacks.Add(1, new KeyValuePair<string, object?>("path", "signing"));
                LogStaleCacheFallback(_logger, "signing", ex);
            }
            else
            {
                _failClosedThrows.Add(1, new KeyValuePair<string, object?>("path", "signing"));
                LogFailClosedNoCacheYet(_logger, "signing", ex);
            }
        }
        finally
        {
            Volatile.Write(ref _signingRefresh, null);
            done.TrySetResult(failure);
        }
    }

    // ─── Observability bootstrap ───────────────────────────────────────────

    private static (ILogger<JwtTokenService> logger, Meter meter, Counter<long> cacheMisses, Counter<long> staleFallbacks, Counter<long> failClosed)
        InitObservability(ILogger<JwtTokenService>? logger, IMeterFactory? meterFactory)
    {
        var resolvedLogger = logger ?? NullLogger<JwtTokenService>.Instance;
        var meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        var cacheMisses = meter.CreateCounter<long>(
            "jwt.key.cache_misses",
            description: "Count of JWT key refreshes started against the key pool, off the request path (tagged by path=signing|validation).");
        var staleFallbacks = meter.CreateCounter<long>(
            "jwt.key.stale_cache_fallbacks",
            description: "Count of failed or rejected key refreshes while a cached value kept serving (an empty or shrunken validation key set counts here).");
        var failClosed = meter.CreateCounter<long>(
            "jwt.key.fail_closed",
            description: "Count of failed key refreshes with no cached value yet (signing: the issuance fails; validation: tokens are refused until a load succeeds).");
        return (resolvedLogger, meter, cacheMisses, staleFallbacks, failClosed);
    }

    [LoggerMessage(EventId = 6101, Level = LogLevel.Debug,
        Message = "JWT active-signing-key cache miss; refreshing from rotation service.")]
    private static partial void LogActiveKeyCacheMiss(ILogger logger);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Debug,
        Message = "JWT validation-keys cache miss; refreshing from rotation service.")]
    private static partial void LogValidationKeyCacheMiss(ILogger logger);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Warning,
        Message = "JWT key cache refresh failed on path={Path}; reusing previously-cached value (Tier-1 stale-cache fallback). Investigate rotation-service availability.")]
    private static partial void LogStaleCacheFallback(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 6104, Level = LogLevel.Error,
        Message = "JWT key cache refresh failed on path={Path} with no prior cache to fall back to (fail-closed: signing fails, tokens are refused until a load succeeds). New pod cold-start under Redis pressure or first-ever request.")]
    private static partial void LogFailClosedNoCacheYet(ILogger logger, string path, Exception exception);

    // ─── Private — credential construction per algorithm ────────────────────

    private static SigningCredentials BuildSigningCredentials(JwtKeyEntry entry)
    {
        return entry.Algorithm switch
        {
            JwtKeyAlgorithm.Hs256 => BuildHmacCredentials(entry),
            JwtKeyAlgorithm.Rs256 => BuildRsaCredentials(entry),
            _ => throw new InvalidOperationException(
                $"Unknown JwtKeyAlgorithm '{entry.Algorithm}' on key '{entry.KeyId}'."),
        };
    }

    private static SigningCredentials BuildHmacCredentials(JwtKeyEntry entry)
    {
        var bytes = Convert.FromBase64String(entry.Key);
        var key = new SymmetricSecurityKey(bytes) { KeyId = entry.KeyId };
        return new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    private static SigningCredentials BuildRsaCredentials(JwtKeyEntry entry)
    {
        var rsa = RSA.Create();
        try
        {
            var pkcs8 = Convert.FromBase64String(entry.Key);
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
        var key = new RsaSecurityKey(rsa) { KeyId = entry.KeyId };
        return new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
    }

    // ─── Private — file-based key loading (R5.4 + earlier path) ────────────

    private static RSA LoadOrCreateFileBasedKey(string dataDirectory, IDataProtectionProvider dataProtection)
    {
        var keyPath = Path.Combine(dataDirectory, LegacyKeyFileName);
        var rsa = RSA.Create(2048);
        var protector = dataProtection.CreateProtector(DataProtectorPurpose);

        if (File.Exists(keyPath))
        {
            var raw = File.ReadAllBytes(keyPath);
            try
            {
                var xmlString = Encoding.UTF8.GetString(protector.Unprotect(raw));
                rsa.FromXmlString(xmlString);
            }
            catch (CryptographicException)
            {
                MigrateLegacyKey(rsa, raw, keyPath, protector);
            }
            catch (FormatException)
            {
                MigrateLegacyKey(rsa, raw, keyPath, protector);
            }
        }
        else
        {
            Directory.CreateDirectory(dataDirectory);
            var xmlString = rsa.ToXmlString(includePrivateParameters: true);
            File.WriteAllBytes(keyPath, protector.Protect(Encoding.UTF8.GetBytes(xmlString)));
        }

        return rsa;
    }

    private static string DeriveKeyIdFromRsa(RSA rsa)
    {
        var publicParams = rsa.ExportParameters(false);
        var modulusHash = SHA256.HashData(publicParams.Modulus!);
        return $"{FileKeyIdPrefix}{Convert.ToHexStringLower(modulusHash.AsSpan(0, 8))}";
    }

    private static TokenValidationParameters BuildSingleKeyValidationParameters(SecurityKey key) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = key,
        ClockSkew = TimeSpan.FromSeconds(30),
        RoleClaimType = "role",
        NameClaimType = "sub",
    };

    private static void MigrateLegacyKey(RSA rsa, byte[] raw, string keyPath, IDataProtector protector)
    {
        var xmlString = Encoding.UTF8.GetString(raw);
        rsa.FromXmlString(xmlString);
        File.WriteAllBytes(keyPath, protector.Protect(Encoding.UTF8.GetBytes(xmlString)));
    }

    // ─── Phase 3.B legacy → rotation-pool migration ────────────────────────
    // Lives in JwtLegacyKeyMigrationService (separate hosted service) to keep
    // this class focused on signing + validation. The constants below are
    // shared with that service.

    internal static string LegacyKeyFilePath(string dataDirectory) =>
        Path.Combine(dataDirectory, LegacyKeyFileName);

    internal static string DataProtectorPurposeForMigration() => DataProtectorPurpose;

    // Internal-visible factories used by JwtLegacyKeyMigrationService when
    // building the migrated JwtKeyEntry — keeps key-id derivation in one place.
    internal static string DeriveKeyIdFromRsaInternal(RSA rsa) => DeriveKeyIdFromRsa(rsa);

    // ─── Cache value records ────────────────────────────────────────────────

    private sealed record CachedActive(
        JwtKeyEntry Entry,
        SigningCredentials Credentials,
        DateTimeOffset CachedAt);

}
