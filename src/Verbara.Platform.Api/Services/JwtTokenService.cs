using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Issues + validates Platform JWTs.
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
///     cached for 60 s (<c>Lazy</c>-style with semaphore guard) so we do not
///     hit Redis on every issuance. Validation uses a multi-key resolver so
///     tokens issued under prior keys still verify during the grace window.
///     A migration shim imports the legacy file as an <c>RS256</c> entry on
///     first boot when the pool is empty + the file exists.</description>
///   </item>
/// </list>
/// <para>
/// Both paths produce externally identical behavior — same claims, same
/// <c>kid</c> header convention, same <see cref="ValidateTokenAsync"/>
/// contract — so endpoints don't care which path is wired.
/// </para>
/// </remarks>
internal sealed class JwtTokenService
{
    private const string Issuer = "verbara-platform";
    private const string Audience = "verbara-platform";
    private const string DataProtectorPurpose = "Verbara.Platform.Jwt.SigningKey";
    private const string LegacyKeyFileName = "jwt-signing-key.xml";
    private const string FileKeyIdPrefix = "platform-jwt-";

    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ImpersonationTokenLifetime = TimeSpan.FromMinutes(30);

    // 5 min — large enough to amortize Redis SCAN+N×GET cost across HPA scale
    // events, small enough that a freshly-rotated key propagates within minutes.
    // Bumped from 60s after R5.5 Phase C-LK observed HPA-induced cold-cache
    // 401 cascades. The key-rotation cadence is 24h ActiveDuration + 24h
    // GracePeriod (48h total validation window), so 5 min cache is still ~570×
    // safer than the rotation cadence. See docs/research/2026-05-24-jti-
    // investigation-presence-vu1500.md § "Recommended fix path Tier 1".
    private static readonly TimeSpan ActiveKeyCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IJtiRevocationCache _revocationCache;

    // ─── File-based path (R5.4 + earlier; tests; single-process bootstrap) ──
    private readonly RsaSecurityKey? _fileSigningKey;
    private readonly SigningCredentials? _fileSigningCredentials;
    private readonly TokenValidationParameters? _fileValidationParameters;

    // ─── Rotation-pool path (AHH Phase 3.B; production multi-replica) ───────
    private readonly IJwtKeyRotationService? _rotationService;
    private readonly TokenValidationParameters? _poolValidationParameters;
    private readonly Lock _cacheLock;
    private CachedActive? _cachedActive;
    private CachedValidationKeys? _cachedValidation;

    // ─── File-based constructor (existing) ──────────────────────────────────

    public JwtTokenService(string dataDirectory, IDataProtectionProvider dataProtection, IJtiRevocationCache revocationCache)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(dataProtection);
        ArgumentNullException.ThrowIfNull(revocationCache);

        _revocationCache = revocationCache;
        _cacheLock = new Lock();

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
        IJtiRevocationCache revocationCache)
    {
        ArgumentNullException.ThrowIfNull(rotationService);
        ArgumentNullException.ThrowIfNull(revocationCache);

        _revocationCache = revocationCache;
        _rotationService = rotationService;
        _cacheLock = new Lock();

        // Validation parameters use a delegate resolver so each call sees the
        // currently-cached set without re-creating the parameters object.
        _poolValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, _, _) => GetCachedValidationKeys(),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role",
            NameClaimType = "sub",
        };
    }

    // ─── Public surface (uniform across both paths) ─────────────────────────

    public TokenValidationParameters ValidationParameters =>
        _rotationService is not null ? _poolValidationParameters! : _fileValidationParameters!;

    public string KeyId => GetActiveSigningCredentials().Key.KeyId!;

    public (string Token, DateTimeOffset ExpiresAt) GenerateAccessToken(User user)
        => GenerateAccessToken(user, null);

    public (string Token, DateTimeOffset ExpiresAt) GenerateAccessToken(User user, IReadOnlySet<string>? permissions)
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

        return SignToken(claims, now, expiresAt);
    }

    public (string Token, DateTimeOffset ExpiresAt) GenerateImpersonationToken(
        User admin, string targetTenantId, IReadOnlySet<string> targetPermissions, bool readOnly = false,
        string? impersonationSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(targetTenantId);
        ArgumentNullException.ThrowIfNull(targetPermissions);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(ImpersonationTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, admin.UserId.Value),
            new("tid", targetTenantId),
            new(JwtRegisteredClaimNames.Email, admin.Email),
            new("name", admin.DisplayName),
            new(ClaimTypes.Role, "Admin"),
            new("impersonator_id", admin.UserId.Value),
            new("impersonator_tenant", admin.TenantId.Value),
            new("impersonation", "true"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
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

        return SignToken(claims, now, expiresAt);
    }

    public async ValueTask<ClaimsPrincipal?> ValidateTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, ValidationParameters, out _);

            var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
            if (jti is not null && await _revocationCache.IsRevokedAsync(jti, ct))
                return null;

            return principal;
        }
        catch
        {
            return null;
        }
    }

    // ─── Private — token signing dispatch ───────────────────────────────────

    private (string Token, DateTimeOffset ExpiresAt) SignToken(
        List<Claim> claims, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        var credentials = GetActiveSigningCredentials();
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

    private SigningCredentials GetActiveSigningCredentials()
    {
        if (_rotationService is null)
            return _fileSigningCredentials!;

        var cached = _cachedActive;
        if (cached is not null && DateTimeOffset.UtcNow - cached.CachedAt < ActiveKeyCacheTtl)
            return cached.Credentials;

        lock (_cacheLock)
        {
            cached = _cachedActive;
            if (cached is not null && DateTimeOffset.UtcNow - cached.CachedAt < ActiveKeyCacheTtl)
                return cached.Credentials;

            try
            {
                var entry = _rotationService.GetActiveSigningKeyAsync().GetAwaiter().GetResult();
                var credentials = BuildSigningCredentials(entry);
                _cachedActive = new CachedActive(entry, credentials, DateTimeOffset.UtcNow);
                // Validation keys must include this entry — invalidate cache so
                // the next validation pull picks it up.
                _cachedValidation = null;
                return credentials;
            }
            catch when (cached is not null)
            {
                // Stale-cache fallback (R5.5 Phase C-LK Tier-1 hardening):
                // a Redis blip or transient timeout in GetActiveSigningKeyAsync
                // would otherwise 401 every in-flight token issuance request on
                // this pod. Reuse the previously-cached active key (its
                // validation entry is still within the 48h rotation window).
                // Worst case: we sign with a key that has been demoted to the
                // grace pool while we couldn't refresh — every validator still
                // accepts it because validation pulls the full grace pool.
                return cached.Credentials;
            }
        }
    }

    private IEnumerable<SecurityKey> GetCachedValidationKeys()
    {
        if (_rotationService is null)
            return [_fileSigningKey!];

        var cached = _cachedValidation;
        if (cached is not null && DateTimeOffset.UtcNow - cached.CachedAt < ActiveKeyCacheTtl)
            return cached.Keys;

        lock (_cacheLock)
        {
            cached = _cachedValidation;
            if (cached is not null && DateTimeOffset.UtcNow - cached.CachedAt < ActiveKeyCacheTtl)
                return cached.Keys;

            try
            {
                var entries = _rotationService.GetValidationKeysAsync().GetAwaiter().GetResult();
                var keys = entries.Select(e => BuildSigningCredentials(e).Key).ToArray();
                _cachedValidation = new CachedValidationKeys(keys, DateTimeOffset.UtcNow);
                return keys;
            }
            catch when (cached is not null)
            {
                // Stale-cache fallback (R5.5 Phase C-LK Tier-1 hardening):
                // a Redis blip in GetValidationKeysAsync would otherwise 401
                // every JwtBearer-authenticated request on this pod. Reusing
                // the previous validation-key set is safe because key
                // rotation is 24h cadence with 24h grace — a momentary
                // failure to refresh cannot expose a missing-key edge case
                // within the 48h validation window. Without this fallback
                // even a 1s Redis timeout cascades to per-pod cold-cache
                // 401 storms during HPA scale-up bursts.
                return cached.Keys;
            }
        }
    }

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

    private sealed record CachedValidationKeys(
        IReadOnlyList<SecurityKey> Keys,
        DateTimeOffset CachedAt);
}
