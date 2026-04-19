# Plan 30B: OIDC SSO Completion

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the OIDC SSO flow with Authorization Code + PKCE + nonce validation and automatic user provisioning.

**Architecture:** New OidcTokenExchangeService handles IdP token endpoint + JWKS discovery. OidcUserProvisioningService creates/updates users from OIDC claims. OidcEndpoints rewritten with PKCE + nonce + encrypted state cookie. User model gets OidcSubject field.

**Tech Stack:** .NET 10 Native AOT, OpenID Connect, PKCE (RFC 7636), RS256/ES256 JWKS.

**Spec:** `docs/superpowers/specs/2026-04-01-v130-integration-compliance-design.md` — Sub-project B.

**Prerequisite:** Plan 30A complete (license enforcement active).

---

### Task 1: Add OIDC auth event types + OidcSubject to User

**Files:**
- Modify: `src/Asterisk.Platform.Identity/AuthEvent.cs`
- Modify: `src/Asterisk.Platform.Identity/User.cs`

- [ ] **Step 1: Add OIDC auth event type constants**

In `src/Asterisk.Platform.Identity/AuthEvent.cs`, add two new constants to the `AuthEventTypes` static class, after the existing `ImpersonationEnded`:

```csharp
public const string OidcLoginSuccess = "oidc_login_success";
public const string OidcLoginFailure = "oidc_login_failure";
```

- [ ] **Step 2: Add OidcSubject property to User**

In `src/Asterisk.Platform.Identity/User.cs`, add after the `ExternalId` property (line 30):

```csharp
public string? OidcSubject { get; set; }
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Asterisk.Platform.Identity/
```

Expected: Build succeeds, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Identity/AuthEvent.cs src/Asterisk.Platform.Identity/User.cs
git commit -m "feat(identity): add OidcSubject to User and OIDC auth event types"
```

---

### Task 2: Add FindByOidcSubjectAsync to IUserStore + implementations

**Files:**
- Modify: `src/Asterisk.Platform.Identity/IUserStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/InMemoryUserStore.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUserStore.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/Migrations/001_InitialSchema.sql`
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/004_OidcSubject.sql`

- [ ] **Step 1: Add FindByOidcSubjectAsync to IUserStore**

In `src/Asterisk.Platform.Identity/IUserStore.cs`, add before the closing brace of the interface:

```csharp
Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct);
```

Full file after edit:

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public interface IUserStore
{
    Task<User?> GetByIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
    Task<User?> GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct);
    Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct);
    Task<PagedResult<User>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);
    Task SaveAsync(User user, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
}
```

- [ ] **Step 2: Implement FindByOidcSubjectAsync in InMemoryUserStore**

In `src/Asterisk.Platform.Storage.InMemory/InMemoryUserStore.cs`, add after `GetByEmailAsync`:

```csharp
public Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct)
{
    var result = _items.Values.FirstOrDefault(u =>
        u.TenantId == tenantId &&
        string.Equals(u.OidcSubject, oidcSubject, StringComparison.Ordinal));

    return Task.FromResult(result);
}
```

- [ ] **Step 3: Implement FindByOidcSubjectAsync in PostgresUserStore**

In `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUserStore.cs`:

First, update `SelectColumns` to include `oidc_subject`:

```csharp
private const string SelectColumns =
    "user_id, tenant_id, email, display_name, role, status, created_at, updated_at, created_by, updated_by, " +
    "password_hash, mfa_enabled, mfa_secret, mfa_recovery_codes, mfa_confirmed_at, email_verified, " +
    "failed_login_attempts, locked_until, password_changed_at, last_login_at, auth_provider, external_id, oidc_subject";
```

Then add the method after `GetByEmailAsync`:

```csharp
public async Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct)
{
    await using var conn = await _dataSource.OpenConnectionAsync(ct);
    var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
        $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId AND oidc_subject = @OidcSubject",
        new { TenantId = tenantId.Value, OidcSubject = oidcSubject });
    return row?.ToUser();
}
```

Update the `UserRow` record to include `oidc_subject`:

```csharp
private sealed record UserRow(
    string user_id,
    string tenant_id,
    string email,
    string display_name,
    int role,
    int status,
    DateTimeOffset created_at,
    DateTimeOffset? updated_at,
    string? created_by,
    string? updated_by,
    string? password_hash,
    bool mfa_enabled,
    string? mfa_secret,
    string[]? mfa_recovery_codes,
    DateTimeOffset? mfa_confirmed_at,
    bool email_verified,
    int failed_login_attempts,
    DateTimeOffset? locked_until,
    DateTimeOffset? password_changed_at,
    DateTimeOffset? last_login_at,
    string auth_provider,
    string? external_id,
    string? oidc_subject)
{
    public User ToUser() => new()
    {
        UserId = EntityId.From(user_id),
        TenantId = new TenantId(tenant_id),
        Email = email,
        DisplayName = display_name,
        Role = (UserRole)role,
        Status = (UserStatus)status,
        CreatedAt = created_at,
        UpdatedAt = updated_at,
        CreatedBy = created_by,
        UpdatedBy = updated_by,
        PasswordHash = password_hash,
        MfaEnabled = mfa_enabled,
        MfaSecret = mfa_secret,
        MfaRecoveryCodes = mfa_recovery_codes,
        MfaConfirmedAt = mfa_confirmed_at,
        EmailVerified = email_verified,
        FailedLoginAttempts = failed_login_attempts,
        LockedUntil = locked_until,
        PasswordChangedAt = password_changed_at,
        LastLoginAt = last_login_at,
        AuthProvider = auth_provider,
        ExternalId = external_id,
        OidcSubject = oidc_subject,
    };
}
```

Update `SaveAsync` to include `oidc_subject` in both INSERT and UPSERT:

```csharp
public async Task SaveAsync(User user, CancellationToken ct)
{
    await using var conn = await _dataSource.OpenConnectionAsync(ct);
    await conn.ExecuteAsync(
        "INSERT INTO users (user_id, tenant_id, email, display_name, role, status, created_at, updated_at, created_by, updated_by, " +
        "password_hash, mfa_enabled, mfa_secret, mfa_recovery_codes, mfa_confirmed_at, email_verified, " +
        "failed_login_attempts, locked_until, password_changed_at, last_login_at, auth_provider, external_id, oidc_subject) " +
        "VALUES (@UserId, @TenantId, @Email, @DisplayName, @Role, @Status, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy, " +
        "@PasswordHash, @MfaEnabled, @MfaSecret, @MfaRecoveryCodes, @MfaConfirmedAt, @EmailVerified, " +
        "@FailedLoginAttempts, @LockedUntil, @PasswordChangedAt, @LastLoginAt, @AuthProvider, @ExternalId, @OidcSubject) " +
        "ON CONFLICT (tenant_id, user_id) DO UPDATE SET " +
        "  display_name = EXCLUDED.display_name, role = EXCLUDED.role, status = EXCLUDED.status, " +
        "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by, " +
        "  password_hash = EXCLUDED.password_hash, mfa_enabled = EXCLUDED.mfa_enabled, " +
        "  mfa_secret = EXCLUDED.mfa_secret, mfa_recovery_codes = EXCLUDED.mfa_recovery_codes, " +
        "  mfa_confirmed_at = EXCLUDED.mfa_confirmed_at, email_verified = EXCLUDED.email_verified, " +
        "  failed_login_attempts = EXCLUDED.failed_login_attempts, locked_until = EXCLUDED.locked_until, " +
        "  password_changed_at = EXCLUDED.password_changed_at, last_login_at = EXCLUDED.last_login_at, " +
        "  auth_provider = EXCLUDED.auth_provider, external_id = EXCLUDED.external_id, " +
        "  oidc_subject = EXCLUDED.oidc_subject",
        new
        {
            UserId = user.UserId.Value,
            TenantId = user.TenantId.Value,
            user.Email,
            user.DisplayName,
            Role = (int)user.Role,
            Status = (int)user.Status,
            user.CreatedAt,
            user.UpdatedAt,
            user.CreatedBy,
            user.UpdatedBy,
            user.PasswordHash,
            user.MfaEnabled,
            user.MfaSecret,
            MfaRecoveryCodes = user.MfaRecoveryCodes?.ToArray(),
            user.MfaConfirmedAt,
            user.EmailVerified,
            user.FailedLoginAttempts,
            user.LockedUntil,
            user.PasswordChangedAt,
            user.LastLoginAt,
            user.AuthProvider,
            user.ExternalId,
            user.OidcSubject,
        });
}
```

- [ ] **Step 4: Update InitialSchema for new deployments**

In `src/Asterisk.Platform.Storage.Postgres/Migrations/001_InitialSchema.sql`, add `oidc_subject TEXT` after `external_id TEXT,` in the `users` CREATE TABLE. Also add an index after `idx_users_email`:

```sql
CREATE INDEX IF NOT EXISTS ix_users_oidc_subject ON users (tenant_id, oidc_subject) WHERE oidc_subject IS NOT NULL;
```

- [ ] **Step 5: Create migration 004 for existing deployments**

Create `src/Asterisk.Platform.Storage.Postgres/Migrations/004_OidcSubject.sql`:

```sql
-- =============================================================================
-- Asterisk.Platform — OIDC Subject Migration (004)
-- =============================================================================
-- Adds oidc_subject column to users table for OIDC SSO user linking.
-- =============================================================================

ALTER TABLE users ADD COLUMN IF NOT EXISTS oidc_subject TEXT;

CREATE INDEX IF NOT EXISTS ix_users_oidc_subject
    ON users (tenant_id, oidc_subject) WHERE oidc_subject IS NOT NULL;
```

- [ ] **Step 6: Verify build**

```bash
dotnet build Asterisk.Platform.slnx
```

Expected: Build succeeds, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Asterisk.Platform.Identity/IUserStore.cs \
        src/Asterisk.Platform.Storage.InMemory/InMemoryUserStore.cs \
        src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUserStore.cs \
        src/Asterisk.Platform.Storage.Postgres/Migrations/001_InitialSchema.sql \
        src/Asterisk.Platform.Storage.Postgres/Migrations/004_OidcSubject.sql
git commit -m "feat(storage): add FindByOidcSubjectAsync to IUserStore with InMemory + Postgres"
```

---

### Task 3: OIDC models and JSON context (AOT-safe)

**Files:**
- Create: `src/Asterisk.Platform.Identity/OidcTokenExchange/OidcModels.cs`

- [ ] **Step 1: Create OidcModels.cs**

Create directory and file `src/Asterisk.Platform.Identity/OidcTokenExchange/OidcModels.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Asterisk.Platform.Identity.OidcTokenExchange;

/// <summary>Response from the IdP token endpoint.</summary>
public sealed record OidcTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("id_token")]
    public string IdToken { get; init; } = "";

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}

/// <summary>Extracted and validated claims from an OIDC ID token.</summary>
public sealed record OidcClaimsResult(
    string Subject,
    string Email,
    string? Name,
    bool EmailVerified);

/// <summary>OpenID Connect discovery document (partial, fields we need).</summary>
public sealed record OidcDiscoveryDocument
{
    [JsonPropertyName("issuer")]
    public string Issuer { get; init; } = "";

    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; init; } = "";

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; init; } = "";

    [JsonPropertyName("jwks_uri")]
    public string JwksUri { get; init; } = "";

    [JsonPropertyName("userinfo_endpoint")]
    public string? UserinfoEndpoint { get; init; }
}

/// <summary>JWKS key set from the IdP (partial, fields we need).</summary>
public sealed record OidcJwksDocument
{
    [JsonPropertyName("keys")]
    public OidcJwk[] Keys { get; init; } = [];
}

/// <summary>Single JWK entry from the JWKS key set.</summary>
public sealed record OidcJwk
{
    [JsonPropertyName("kty")]
    public string Kty { get; init; } = "";

    [JsonPropertyName("use")]
    public string? Use { get; init; }

    [JsonPropertyName("kid")]
    public string? Kid { get; init; }

    [JsonPropertyName("alg")]
    public string? Alg { get; init; }

    [JsonPropertyName("n")]
    public string? N { get; init; }

    [JsonPropertyName("e")]
    public string? E { get; init; }

    [JsonPropertyName("x")]
    public string? X { get; init; }

    [JsonPropertyName("y")]
    public string? Y { get; init; }

    [JsonPropertyName("crv")]
    public string? Crv { get; init; }
}

/// <summary>Cookie state stored during the OIDC authorization code flow.</summary>
public sealed record OidcFlowState
{
    public string CodeVerifier { get; init; } = "";
    public string Nonce { get; init; } = "";
    public string TenantId { get; init; } = "";
    public string? ReturnUrl { get; init; }
    public long ExpiresAtUnix { get; init; }
}

/// <summary>
/// AOT-safe JSON serializer context for all OIDC models.
/// </summary>
[JsonSerializable(typeof(OidcTokenResponse))]
[JsonSerializable(typeof(OidcDiscoveryDocument))]
[JsonSerializable(typeof(OidcJwksDocument))]
[JsonSerializable(typeof(OidcJwk))]
[JsonSerializable(typeof(OidcJwk[]))]
[JsonSerializable(typeof(OidcFlowState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class OidcJsonContext : JsonSerializerContext;
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/Asterisk.Platform.Identity/
```

Expected: Build succeeds, 0 warnings. The source generator produces `OidcJsonContext` at compile time.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Identity/OidcTokenExchange/
git commit -m "feat(identity): add OIDC models and AOT-safe JSON context"
```

---

### Task 4: OidcTokenExchangeService

**Files:**
- Create: `src/Asterisk.Platform.Identity/OidcTokenExchange/IOidcTokenExchangeService.cs`
- Create: `src/Asterisk.Platform.Identity/OidcTokenExchange/OidcTokenExchangeService.cs`
- Modify: `src/Asterisk.Platform.Identity/Asterisk.Platform.Identity.csproj`

- [ ] **Step 1: Add System.IdentityModel.Tokens.Jwt to Identity project**

In `src/Asterisk.Platform.Identity/Asterisk.Platform.Identity.csproj`, add to the `<PackageReference>` ItemGroup:

```xml
<PackageReference Include="System.IdentityModel.Tokens.Jwt" />
<PackageReference Include="Microsoft.IdentityModel.Tokens" />
<PackageReference Include="Microsoft.Extensions.Http" />
```

Note: Central package management in `Directory.Packages.props` already pins versions.

- [ ] **Step 2: Create IOidcTokenExchangeService.cs**

Create `src/Asterisk.Platform.Identity/OidcTokenExchange/IOidcTokenExchangeService.cs`:

```csharp
namespace Asterisk.Platform.Identity.OidcTokenExchange;

/// <summary>
/// Exchanges an authorization code for tokens at an OIDC IdP and validates the resulting ID token.
/// </summary>
public interface IOidcTokenExchangeService
{
    /// <summary>
    /// Exchanges an authorization code for an OIDC token response.
    /// </summary>
    Task<OidcTokenResponse> ExchangeCodeAsync(
        string authority,
        string code,
        string codeVerifier,
        string redirectUri,
        string clientId,
        string clientSecret,
        CancellationToken ct);

    /// <summary>
    /// Validates an ID token JWT: verifies signature via JWKS, checks issuer/audience/expiry/nonce.
    /// </summary>
    Task<OidcClaimsResult> ValidateIdTokenAsync(
        string idToken,
        string authority,
        string expectedAudience,
        string expectedNonce,
        CancellationToken ct);
}
```

- [ ] **Step 3: Create OidcTokenExchangeService.cs**

Create `src/Asterisk.Platform.Identity/OidcTokenExchange/OidcTokenExchangeService.cs`:

```csharp
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Asterisk.Platform.Identity.OidcTokenExchange;

/// <summary>
/// Handles OIDC Authorization Code exchange and ID token validation.
/// Uses IHttpClientFactory for outbound calls and caches JWKS/discovery per authority.
/// </summary>
public sealed class OidcTokenExchangeService : IOidcTokenExchangeService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OidcTokenExchangeService> _logger;
    private readonly ConcurrentDictionary<string, CachedDiscovery> _discoveryCache = new();
    private readonly ConcurrentDictionary<string, CachedJwks> _jwksCache = new();
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan JwksCacheTtl = TimeSpan.FromHours(24);

    public OidcTokenExchangeService(
        IHttpClientFactory httpClientFactory,
        ILogger<OidcTokenExchangeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<OidcTokenResponse> ExchangeCodeAsync(
        string authority,
        string code,
        string codeVerifier,
        string redirectUri,
        string clientId,
        string clientSecret,
        CancellationToken ct)
    {
        var discovery = await GetDiscoveryAsync(authority, ct);

        var client = _httpClientFactory.CreateClient("oidc");
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code_verifier"] = codeVerifier,
        });

        var response = await client.PostAsync(discovery.TokenEndpoint, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        var tokenResponse = JsonSerializer.Deserialize(json, OidcJsonContext.Default.OidcTokenResponse);
        if (tokenResponse is null)
            throw new InvalidOperationException("Failed to deserialize OIDC token response");

        if (!string.IsNullOrEmpty(tokenResponse.Error))
            throw new InvalidOperationException(
                $"OIDC token exchange failed: {tokenResponse.Error} — {tokenResponse.ErrorDescription}");

        return tokenResponse;
    }

    public async Task<OidcClaimsResult> ValidateIdTokenAsync(
        string idToken,
        string authority,
        string expectedAudience,
        string expectedNonce,
        CancellationToken ct)
    {
        var discovery = await GetDiscoveryAsync(authority, ct);
        var jwks = await GetJwksAsync(discovery.JwksUri, authority, forceRefresh: false, ct);

        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = discovery.Issuer,
            ValidateAudience = true,
            ValidAudience = expectedAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwks,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(idToken, validationParameters, out _);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // Key rotation: force refresh JWKS and retry once
            _logger.LogInformation("JWKS key not found for authority {Authority}, forcing refresh", authority);
            jwks = await GetJwksAsync(discovery.JwksUri, authority, forceRefresh: true, ct);
            validationParameters.IssuerSigningKeys = jwks;
            principal = handler.ValidateToken(idToken, validationParameters, out _);
        }

        // Validate nonce
        var nonceClaim = principal.FindFirst("nonce")?.Value;
        if (!string.Equals(nonceClaim, expectedNonce, StringComparison.Ordinal))
            throw new SecurityTokenValidationException(
                "ID token nonce does not match expected value — possible replay attack");

        // Extract claims
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new SecurityTokenValidationException("ID token missing sub claim");

        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? throw new SecurityTokenValidationException("ID token missing email claim");

        var name = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value;

        var emailVerifiedClaim = principal.FindFirst("email_verified")?.Value;
        var emailVerified = string.Equals(emailVerifiedClaim, "true", StringComparison.OrdinalIgnoreCase);

        return new OidcClaimsResult(subject, email, name, emailVerified);
    }

    // ─── PKCE helpers (static, used by OidcEndpoints) ────────────────────────

    /// <summary>Generates a cryptographically random code verifier (RFC 7636, 43-128 chars).</summary>
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>Computes the S256 code challenge from a code verifier.</summary>
    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>Generates a cryptographically random nonce.</summary>
    public static string GenerateNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    // ─── Discovery + JWKS caching ───────────────────────────────────────────

    private async Task<OidcDiscoveryDocument> GetDiscoveryAsync(string authority, CancellationToken ct)
    {
        var key = authority.TrimEnd('/');
        if (_discoveryCache.TryGetValue(key, out var cached) && !cached.IsExpired)
            return cached.Document;

        var client = _httpClientFactory.CreateClient("oidc");
        var url = $"{key}/.well-known/openid-configuration";
        var json = await client.GetStringAsync(url, ct);

        var doc = JsonSerializer.Deserialize(json, OidcJsonContext.Default.OidcDiscoveryDocument)
            ?? throw new InvalidOperationException($"Failed to fetch OIDC discovery document from {url}");

        _discoveryCache[key] = new CachedDiscovery(doc, DateTimeOffset.UtcNow.Add(DiscoveryCacheTtl));
        return doc;
    }

    private async Task<IReadOnlyList<SecurityKey>> GetJwksAsync(
        string jwksUri, string authority, bool forceRefresh, CancellationToken ct)
    {
        var cacheKey = authority.TrimEnd('/');
        if (!forceRefresh && _jwksCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
            return cached.Keys;

        var client = _httpClientFactory.CreateClient("oidc");
        var json = await client.GetStringAsync(jwksUri, ct);

        var jwksDoc = JsonSerializer.Deserialize(json, OidcJsonContext.Default.OidcJwksDocument)
            ?? throw new InvalidOperationException($"Failed to fetch JWKS from {jwksUri}");

        var keys = new List<SecurityKey>();
        foreach (var jwk in jwksDoc.Keys)
        {
            if (jwk.Use is not null and not "sig")
                continue;

            var securityKey = ConvertJwkToSecurityKey(jwk);
            if (securityKey is not null)
            {
                securityKey.KeyId = jwk.Kid;
                keys.Add(securityKey);
            }
        }

        _jwksCache[cacheKey] = new CachedJwks(keys, DateTimeOffset.UtcNow.Add(JwksCacheTtl));
        return keys;
    }

    private static SecurityKey? ConvertJwkToSecurityKey(OidcJwk jwk)
    {
        if (jwk.Kty == "RSA" && jwk.N is not null && jwk.E is not null)
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64UrlDecode(jwk.N),
                Exponent = Base64UrlDecode(jwk.E),
            });
            return new RsaSecurityKey(rsa);
        }

        if (jwk.Kty == "EC" && jwk.X is not null && jwk.Y is not null && jwk.Crv is not null)
        {
            var curve = jwk.Crv switch
            {
                "P-256" => ECCurve.NamedCurves.nistP256,
                "P-384" => ECCurve.NamedCurves.nistP384,
                "P-521" => ECCurve.NamedCurves.nistP521,
                _ => default,
            };
            if (curve.Oid is null)
                return null;

            var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = curve,
                Q = new ECPoint
                {
                    X = Base64UrlDecode(jwk.X),
                    Y = Base64UrlDecode(jwk.Y),
                },
            });
            return new ECDsaSecurityKey(ecdsa);
        }

        return null;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var output = input.Replace('-', '+').Replace('_', '/');
        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }
        return Convert.FromBase64String(output);
    }

    // ─── Cache entries ──────────────────────────────────────────────────────

    private sealed record CachedDiscovery(OidcDiscoveryDocument Document, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }

    private sealed record CachedJwks(IReadOnlyList<SecurityKey> Keys, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
```

- [ ] **Step 4: Verify build**

```bash
dotnet build src/Asterisk.Platform.Identity/
```

Expected: Build succeeds, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Identity/OidcTokenExchange/ \
        src/Asterisk.Platform.Identity/Asterisk.Platform.Identity.csproj
git commit -m "feat(identity): add OidcTokenExchangeService with PKCE + JWKS discovery"
```

---

### Task 5: OidcUserProvisioningService

**Files:**
- Create: `src/Asterisk.Platform.Identity/OidcTokenExchange/IOidcUserProvisioningService.cs`
- Create: `src/Asterisk.Platform.Identity/OidcTokenExchange/OidcUserProvisioningService.cs`

- [ ] **Step 1: Create IOidcUserProvisioningService.cs**

Create `src/Asterisk.Platform.Identity/OidcTokenExchange/IOidcUserProvisioningService.cs`:

```csharp
namespace Asterisk.Platform.Identity.OidcTokenExchange;

/// <summary>
/// Creates or updates a user based on OIDC claims.
/// Respects the tenant's OidcAutoCreateUsers and OidcDefaultRole settings.
/// </summary>
public interface IOidcUserProvisioningService
{
    /// <summary>
    /// Finds an existing user by OIDC subject, or creates a new one if auto-provisioning is enabled.
    /// Returns null if auto-create is disabled and the user does not exist.
    /// </summary>
    Task<User?> ProvisionOrUpdateAsync(
        string tenantId,
        OidcClaimsResult claims,
        TenantAuthConfig config,
        CancellationToken ct);
}
```

- [ ] **Step 2: Create OidcUserProvisioningService.cs**

Create `src/Asterisk.Platform.Identity/OidcTokenExchange/OidcUserProvisioningService.cs`:

```csharp
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Identity.OidcTokenExchange;

public sealed class OidcUserProvisioningService : IOidcUserProvisioningService
{
    private readonly IUserStore _userStore;
    private readonly ILogger<OidcUserProvisioningService> _logger;

    public OidcUserProvisioningService(
        IUserStore userStore,
        ILogger<OidcUserProvisioningService> logger)
    {
        _userStore = userStore;
        _logger = logger;
    }

    public async Task<User?> ProvisionOrUpdateAsync(
        string tenantId,
        OidcClaimsResult claims,
        TenantAuthConfig config,
        CancellationToken ct)
    {
        var tid = new TenantId(tenantId);

        // 1. Look up by OIDC subject (primary identifier from IdP)
        var user = await _userStore.FindByOidcSubjectAsync(tid, claims.Subject, ct);

        if (user is not null)
        {
            // Update display name and email if changed
            var needsUpdate = false;

            if (claims.Name is not null && !string.Equals(user.DisplayName, claims.Name, StringComparison.Ordinal))
            {
                user.DisplayName = claims.Name;
                needsUpdate = true;
            }

            if (claims.EmailVerified && user.EmailVerified != claims.EmailVerified)
            {
                user.EmailVerified = true;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await _userStore.SaveAsync(user, ct);
            }

            user.LastLoginAt = DateTimeOffset.UtcNow;
            await _userStore.SaveAsync(user, ct);

            _logger.LogInformation(
                "OIDC user {Subject} matched existing user {UserId} in tenant {TenantId}",
                claims.Subject, user.UserId.Value, tenantId);

            return user;
        }

        // 2. Fallback: look up by email (for users created via admin panel before OIDC linking)
        user = await _userStore.GetByEmailAsync(tid, claims.Email, ct);

        if (user is not null)
        {
            // Link the existing user to this OIDC subject
            user.OidcSubject = claims.Subject;
            user.AuthProvider = "oidc";
            user.EmailVerified = user.EmailVerified || claims.EmailVerified;
            user.LastLoginAt = DateTimeOffset.UtcNow;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _userStore.SaveAsync(user, ct);

            _logger.LogInformation(
                "OIDC user {Subject} linked to existing user {UserId} by email in tenant {TenantId}",
                claims.Subject, user.UserId.Value, tenantId);

            return user;
        }

        // 3. Auto-create new user if enabled
        if (!config.OidcAutoCreateUsers)
        {
            _logger.LogWarning(
                "OIDC user {Subject} ({Email}) not found and auto-create is disabled for tenant {TenantId}",
                claims.Subject, claims.Email, tenantId);
            return null;
        }

        var role = Enum.TryParse<UserRole>(config.OidcDefaultRole, ignoreCase: true, out var parsed)
            ? parsed
            : UserRole.Agent;

        var newUser = new User
        {
            UserId = EntityId.NewId(),
            TenantId = tid,
            Email = claims.Email,
            DisplayName = claims.Name ?? claims.Email,
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            AuthProvider = "oidc",
            OidcSubject = claims.Subject,
            EmailVerified = claims.EmailVerified,
            LastLoginAt = DateTimeOffset.UtcNow,
        };

        await _userStore.SaveAsync(newUser, ct);

        _logger.LogInformation(
            "OIDC auto-created user {UserId} ({Email}) with role {Role} in tenant {TenantId}",
            newUser.UserId.Value, claims.Email, role, tenantId);

        return newUser;
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Asterisk.Platform.Identity/
```

Expected: Build succeeds, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Identity/OidcTokenExchange/IOidcUserProvisioningService.cs \
        src/Asterisk.Platform.Identity/OidcTokenExchange/OidcUserProvisioningService.cs
git commit -m "feat(identity): add OidcUserProvisioningService for auto-provisioning"
```

---

### Task 6: Rewrite OidcEndpoints with PKCE + nonce + encrypted cookie

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/OidcEndpoints.cs`

- [ ] **Step 1: Rewrite OidcEndpoints.cs**

Replace the entire file `src/Asterisk.Platform.Api/Endpoints/OidcEndpoints.cs` with:

```csharp
using System.Security.Claims;
using System.Text.Json;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Identity.OidcTokenExchange;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class OidcEndpoints
{
    private const string StateCookieName = "oidc_state";
    private const string ProtectorPurpose = "OidcFlowState";
    private static readonly TimeSpan FlowTimeout = TimeSpan.FromMinutes(5);

    public static void MapOidcEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/oidc");

        group.MapGet("/login", OidcLogin).AllowAnonymous();
        group.MapGet("/callback", OidcCallback).AllowAnonymous();
        group.MapPost("/logout", OidcLogout).RequireAuthorization();
    }

    // ─── Login: build PKCE + nonce, store in encrypted cookie, redirect ─────

    private static async Task<IResult> OidcLogin(
        HttpContext context,
        [FromServices] ITenantAuthConfigStore configStore,
        [FromServices] IDataProtectionProvider dataProtection,
        string? tenant_id,
        string? return_url,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant_id))
            return Results.BadRequest(new ErrorResponse("tenant_id query parameter is required"));

        var config = await configStore.GetAsync(tenant_id, ct);
        if (config is null || !config.OidcEnabled || string.IsNullOrEmpty(config.OidcAuthority))
            return Results.BadRequest(new ErrorResponse("OIDC is not enabled for this tenant"));

        // Generate PKCE pair (RFC 7636)
        var codeVerifier = OidcTokenExchangeService.GenerateCodeVerifier();
        var codeChallenge = OidcTokenExchangeService.ComputeCodeChallenge(codeVerifier);

        // Generate nonce (binds ID token to this flow, prevents replay)
        var nonce = OidcTokenExchangeService.GenerateNonce();

        // Random state parameter (CSRF protection, not overloaded with tenant_id)
        var state = OidcTokenExchangeService.GenerateNonce();

        // Store flow parameters in encrypted cookie
        var flowState = new OidcFlowState
        {
            CodeVerifier = codeVerifier,
            Nonce = nonce,
            TenantId = tenant_id,
            ReturnUrl = return_url,
            ExpiresAtUnix = DateTimeOffset.UtcNow.Add(FlowTimeout).ToUnixTimeSeconds(),
        };

        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        var serialized = JsonSerializer.Serialize(flowState, OidcJsonContext.Default.OidcFlowState);
        var encrypted = protector.Protect(serialized);

        context.Response.Cookies.Append(StateCookieName, encrypted, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax, // Lax required: IdP redirects back with GET
            Path = "/api/auth/oidc",
            MaxAge = FlowTimeout,
        });

        // Build authorization URL
        var redirectUri = $"{context.Request.Scheme}://{context.Request.Host}/api/auth/oidc/callback";
        var authorizationUrl = $"{config.OidcAuthority.TrimEnd('/')}/authorize" +
            $"?client_id={Uri.EscapeDataString(config.OidcClientId ?? "")}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope=openid%20profile%20email" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&nonce={Uri.EscapeDataString(nonce)}" +
            $"&state={Uri.EscapeDataString(state)}";

        return Results.Redirect(authorizationUrl);
    }

    // ─── Callback: exchange code, validate ID token, provision user, issue JWT ─

    private static async Task<IResult> OidcCallback(
        HttpContext context,
        [FromServices] ITenantAuthConfigStore configStore,
        [FromServices] IUserStore userStore,
        [FromServices] IOidcTokenExchangeService tokenExchange,
        [FromServices] IOidcUserProvisioningService provisioning,
        [FromServices] IDataProtectionProvider dataProtection,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
        AuthEventService authEvents,
        string? code,
        string? error,
        string? error_description,
        CancellationToken ct)
    {
        // ─── Read and delete the encrypted state cookie ─────────────────
        var encryptedState = context.Request.Cookies[StateCookieName];
        context.Response.Cookies.Delete(StateCookieName, new CookieOptions
        {
            Path = "/api/auth/oidc",
        });

        if (string.IsNullOrEmpty(encryptedState))
            return Results.BadRequest(new ErrorResponse("Missing OIDC state cookie — flow may have expired"));

        OidcFlowState flowState;
        try
        {
            var protector = dataProtection.CreateProtector(ProtectorPurpose);
            var decrypted = protector.Unprotect(encryptedState);
            flowState = JsonSerializer.Deserialize(decrypted, OidcJsonContext.Default.OidcFlowState)
                ?? throw new InvalidOperationException("Null deserialization");
        }
        catch
        {
            return Results.BadRequest(new ErrorResponse("Invalid OIDC state cookie — possible tampering"));
        }

        // Check expiry
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > flowState.ExpiresAtUnix)
            return Results.BadRequest(new ErrorResponse("OIDC flow expired — please try again"));

        var tenantId = flowState.TenantId;
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var ua = context.Request.Headers.UserAgent.FirstOrDefault();

        // ─── Handle IdP errors ──────────────────────────────────────────
        if (!string.IsNullOrEmpty(error))
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua,
                new OidcEventDetail("idp_error", error, error_description), ct);
            return Results.BadRequest(new ErrorResponse($"OIDC provider error: {error}"));
        }

        if (string.IsNullOrEmpty(code))
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua,
                new OidcEventDetail("missing_code"), ct);
            return Results.BadRequest(new ErrorResponse("Missing authorization code"));
        }

        // ─── Load tenant config ─────────────────────────────────────────
        var config = await configStore.GetAsync(tenantId, ct);
        if (config is null || !config.OidcEnabled)
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua,
                new OidcEventDetail("config_invalid"), ct);
            return Results.BadRequest(new ErrorResponse("OIDC is not enabled for this tenant"));
        }

        // ─── Exchange authorization code for tokens ─────────────────────
        var redirectUri = $"{context.Request.Scheme}://{context.Request.Host}/api/auth/oidc/callback";

        OidcTokenResponse tokenResponse;
        try
        {
            tokenResponse = await tokenExchange.ExchangeCodeAsync(
                config.OidcAuthority!,
                code,
                flowState.CodeVerifier,
                redirectUri,
                config.OidcClientId ?? "",
                config.OidcClientSecret ?? "",
                ct);
        }
        catch (Exception ex)
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua,
                new OidcEventDetail("token_exchange_failed", ex.Message), ct);
            return Results.BadRequest(new ErrorResponse("OIDC token exchange failed"));
        }

        // ─── Validate ID token (signature, issuer, audience, nonce) ─────
        OidcClaimsResult claims;
        try
        {
            claims = await tokenExchange.ValidateIdTokenAsync(
                tokenResponse.IdToken,
                config.OidcAuthority!,
                config.OidcClientId ?? "",
                flowState.Nonce,
                ct);
        }
        catch (Exception ex)
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua,
                new OidcEventDetail("id_token_validation_failed", ex.Message), ct);
            return Results.BadRequest(new ErrorResponse("OIDC ID token validation failed"));
        }

        // ─── Provision or update user ───────────────────────────────────
        var user = await provisioning.ProvisionOrUpdateAsync(tenantId, claims, config, ct);
        if (user is null)
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua,
                new OidcEventDetail("user_not_found", claims.Email), ct);
            return Results.Unauthorized();
        }

        if (user.Status != UserStatus.Active)
        {
            await authEvents.LogAsync(tenantId, user.UserId.Value, AuthEventTypes.OidcLoginFailure, ip, ua,
                new OidcEventDetail("user_inactive"), ct);
            return Results.Unauthorized();
        }

        // ─── Generate Platform JWT + refresh token ──────────────────────
        IReadOnlySet<string>? permissions = null;
        var resolver = context.RequestServices.GetService<PermissionResolver>();
        if (resolver is not null)
        {
            try { permissions = await resolver.ResolveAsync(user.TenantId, user.UserId, ct); }
            catch { /* permissions resolved at authorization time */ }
        }

        var effectivePermissions = permissions?.ToArray() ?? [];
        if (effectivePermissions.Length == 0)
        {
            effectivePermissions = user.Role switch
            {
                UserRole.Admin => RoleDefaultPermissions.Admin,
                UserRole.Supervisor => RoleDefaultPermissions.Supervisor,
                UserRole.Agent => RoleDefaultPermissions.Agent,
                _ => [],
            };
        }

        var (accessToken, expiresAt) = jwtService.GenerateAccessToken(user, permissions);
        var (rawRefreshToken, _) = await refreshService.GenerateAsync(
            user.UserId.Value, user.TenantId.Value, ip, ua, ct);

        // Set refresh cookie
        context.Response.Cookies.Append("refresh_token", rawRefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            MaxAge = TimeSpan.FromDays(7),
        });

        await authEvents.LogAsync(tenantId, user.UserId.Value, AuthEventTypes.OidcLoginSuccess, ip, ua,
            new OidcEventDetail("success"), ct);

        // ─── Redirect to frontend with token ────────────────────────────
        // The frontend reads the access token from the URL fragment and stores it.
        // This avoids exposing the token in server logs (fragment is not sent to server).
        var returnUrl = flowState.ReturnUrl ?? "/";
        var featureRegistry = context.RequestServices.GetService<IFeatureRegistry>();
        var features = featureRegistry?.GetFeatures() ?? new Dictionary<string, bool>();

        // Build the callback URL that the frontend SPA will intercept
        var callbackUrl = $"{returnUrl}#oidc_callback" +
            $"&access_token={Uri.EscapeDataString(accessToken)}" +
            $"&expires_at={Uri.EscapeDataString(expiresAt.ToString("O"))}" +
            $"&tenant_id={Uri.EscapeDataString(tenantId)}" +
            $"&user_id={Uri.EscapeDataString(user.UserId.Value)}" +
            $"&email={Uri.EscapeDataString(user.Email)}" +
            $"&display_name={Uri.EscapeDataString(user.DisplayName)}" +
            $"&role={Uri.EscapeDataString(user.Role.ToString().ToLowerInvariant())}";

        return Results.Redirect(callbackUrl);
    }

    // ─── Logout ─────────────────────────────────────────────────────────────

    private static async Task<IResult> OidcLogout(
        HttpContext context,
        RefreshTokenService refreshService,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        var userId = context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        var rawToken = context.Request.Cookies["refresh_token"];
        if (!string.IsNullOrEmpty(rawToken))
            await refreshService.RevokeAsync(rawToken, ct);

        if (tenantId is not null)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString();
            var ua = context.Request.Headers.UserAgent.FirstOrDefault();
            await authEvents.LogAsync(tenantId, userId, "oidc_logout", ip, ua, null, ct);
        }

        context.Response.Cookies.Delete("refresh_token");
        return Results.Ok(new MessageResponse("Logged out"));
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record OidcEventDetail(
    string Action,
    string? Detail = null,
    string? Description = null);
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/Asterisk.Platform.Api/
```

Expected: Build succeeds, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/OidcEndpoints.cs
git commit -m "feat(api): rewrite OidcEndpoints with PKCE + nonce + encrypted state cookie"
```

---

### Task 7: DI wiring in Program.cs + ApiJsonContext registrations

**Files:**
- Modify: `src/Asterisk.Platform.Api/Program.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

- [ ] **Step 1: Register OIDC services and IHttpClientFactory in Program.cs**

In `src/Asterisk.Platform.Api/Program.cs`, after the `builder.Services.AddSingleton<SessionService>();` line (line 104), add:

```csharp
// ─── OIDC SSO Services ──────────────────────────────────────────────────────
builder.Services.AddHttpClient("oidc");
builder.Services.AddDataProtection();
builder.Services.AddSingleton<IOidcTokenExchangeService, OidcTokenExchangeService>();
builder.Services.AddSingleton<IOidcUserProvisioningService, OidcUserProvisioningService>();
```

Also add the required using at the top of Program.cs:

```csharp
using Asterisk.Platform.Identity.OidcTokenExchange;
```

- [ ] **Step 2: Register OidcEventDetail in ApiJsonContext**

In `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`, add after the `[JsonSerializable(typeof(AuthEventDetail))]` line:

```csharp
[JsonSerializable(typeof(OidcEventDetail))]
```

- [ ] **Step 3: Verify full solution build**

```bash
dotnet build Asterisk.Platform.slnx
```

Expected: Build succeeds, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Api/Program.cs \
        src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "feat(api): register OIDC services in DI and add OidcEventDetail to ApiJsonContext"
```

---

### Task 8: Tests — OidcTokenExchangeService

**Files:**
- Create: `tests/Asterisk.Platform.Identity.Tests/OidcTokenExchangeServiceTests.cs`

- [ ] **Step 1: Create OidcTokenExchangeServiceTests.cs**

Create `tests/Asterisk.Platform.Identity.Tests/OidcTokenExchangeServiceTests.cs`:

```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asterisk.Platform.Identity.OidcTokenExchange;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Asterisk.Platform.Identity.Tests;

public sealed class OidcTokenExchangeServiceTests
{
    // ─── PKCE Tests ─────────────────────────────────────────────────────────

    [Fact]
    public void GenerateCodeVerifier_ShouldReturnBase64UrlString()
    {
        var verifier = OidcTokenExchangeService.GenerateCodeVerifier();

        verifier.Should().NotBeNullOrWhiteSpace();
        verifier.Length.Should().BeGreaterOrEqualTo(43);
        verifier.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Fact]
    public void GenerateCodeVerifier_ShouldProduceUniqueValues()
    {
        var v1 = OidcTokenExchangeService.GenerateCodeVerifier();
        var v2 = OidcTokenExchangeService.GenerateCodeVerifier();

        v1.Should().NotBe(v2);
    }

    [Fact]
    public void ComputeCodeChallenge_ShouldReturnDeterministicS256Hash()
    {
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var challenge = OidcTokenExchangeService.ComputeCodeChallenge(verifier);

        // RFC 7636 Appendix B test vector
        challenge.Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void GenerateNonce_ShouldReturnUniqueBase64UrlString()
    {
        var n1 = OidcTokenExchangeService.GenerateNonce();
        var n2 = OidcTokenExchangeService.GenerateNonce();

        n1.Should().NotBeNullOrWhiteSpace();
        n2.Should().NotBeNullOrWhiteSpace();
        n1.Should().NotBe(n2);
    }

    // ─── Token Exchange Tests ───────────────────────────────────────────────

    [Fact]
    public async Task ExchangeCodeAsync_ShouldPostToTokenEndpoint()
    {
        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var tokenResponseJson = JsonSerializer.Serialize(new OidcTokenResponse
        {
            AccessToken = "at-123",
            IdToken = "id-token-jwt",
            TokenType = "Bearer",
            ExpiresIn = 3600,
        }, OidcJsonContext.Default.OidcTokenResponse);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/oauth/token"] = tokenResponseJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var result = await service.ExchangeCodeAsync(
            "https://idp.example.com",
            "auth-code-123",
            "code-verifier-abc",
            "https://app.example.com/callback",
            "client-id",
            "client-secret",
            CancellationToken.None);

        result.AccessToken.Should().Be("at-123");
        result.IdToken.Should().Be("id-token-jwt");
    }

    [Fact]
    public async Task ExchangeCodeAsync_ShouldThrow_WhenIdpReturnsError()
    {
        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var errorJson = JsonSerializer.Serialize(new OidcTokenResponse
        {
            Error = "invalid_grant",
            ErrorDescription = "The authorization code has expired",
        }, OidcJsonContext.Default.OidcTokenResponse);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/oauth/token"] = errorJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var act = () => service.ExchangeCodeAsync(
            "https://idp.example.com",
            "expired-code",
            "verifier",
            "https://app.example.com/callback",
            "client-id",
            "client-secret",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid_grant*");
    }

    // ─── ID Token Validation Tests ──────────────────────────────────────────

    [Fact]
    public async Task ValidateIdTokenAsync_ShouldExtractClaims_WhenTokenIsValid()
    {
        var rsa = RSA.Create(2048);
        var kid = "test-key-1";
        var nonce = "test-nonce-123";

        var idToken = CreateTestIdToken(rsa, kid, nonce,
            issuer: "https://idp.example.com",
            audience: "client-id",
            email: "user@example.com",
            subject: "oidc-sub-abc",
            name: "Test User");

        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var jwksJson = CreateJwksJson(rsa, kid);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/.well-known/jwks.json"] = jwksJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var claims = await service.ValidateIdTokenAsync(
            idToken, "https://idp.example.com", "client-id", nonce, CancellationToken.None);

        claims.Subject.Should().Be("oidc-sub-abc");
        claims.Email.Should().Be("user@example.com");
        claims.Name.Should().Be("Test User");
    }

    [Fact]
    public async Task ValidateIdTokenAsync_ShouldThrow_WhenNonceMismatch()
    {
        var rsa = RSA.Create(2048);
        var kid = "test-key-1";

        var idToken = CreateTestIdToken(rsa, kid, "actual-nonce",
            issuer: "https://idp.example.com",
            audience: "client-id",
            email: "user@example.com",
            subject: "sub-1");

        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var jwksJson = CreateJwksJson(rsa, kid);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/.well-known/jwks.json"] = jwksJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var act = () => service.ValidateIdTokenAsync(
            idToken, "https://idp.example.com", "client-id", "wrong-nonce", CancellationToken.None);

        await act.Should().ThrowAsync<SecurityTokenValidationException>()
            .WithMessage("*nonce*");
    }

    [Fact]
    public async Task ValidateIdTokenAsync_ShouldThrow_WhenAudienceMismatch()
    {
        var rsa = RSA.Create(2048);
        var kid = "test-key-1";
        var nonce = "test-nonce";

        var idToken = CreateTestIdToken(rsa, kid, nonce,
            issuer: "https://idp.example.com",
            audience: "wrong-client-id",
            email: "user@example.com",
            subject: "sub-1");

        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var jwksJson = CreateJwksJson(rsa, kid);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/.well-known/jwks.json"] = jwksJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var act = () => service.ValidateIdTokenAsync(
            idToken, "https://idp.example.com", "correct-client-id", nonce, CancellationToken.None);

        await act.Should().ThrowAsync<SecurityTokenValidationException>();
    }

    // ─── Test Helpers ───────────────────────────────────────────────────────

    private static string CreateTestIdToken(
        RSA rsa, string kid, string nonce,
        string issuer, string audience, string email, string subject, string? name = null)
    {
        var securityKey = new RsaSecurityKey(rsa) { KeyId = kid };
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Email, email),
            new("nonce", nonce),
            new("email_verified", "true"),
        };
        if (name is not null)
            claims.Add(new Claim("name", name));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(1),
            IssuedAt = DateTime.UtcNow,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = credentials,
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.CreateEncodedJwt(descriptor);
    }

    private static string CreateJwksJson(RSA rsa, string kid)
    {
        var parameters = rsa.ExportParameters(false);
        var n = Base64UrlEncode(parameters.Modulus!);
        var e = Base64UrlEncode(parameters.Exponent!);

        return $$"""
        {
          "keys": [
            {
              "kty": "RSA",
              "use": "sig",
              "kid": "{{kid}}",
              "alg": "RS256",
              "n": "{{n}}",
              "e": "{{e}}"
            }
          ]
        }
        """;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// ─── Test doubles ────────────────────────────────────────────────────────────

internal sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;

    public MockHttpHandler(Dictionary<string, string> responses) => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        // For POST requests (token endpoint), match by URL without query
        var matchUrl = request.Method == HttpMethod.Post
            ? request.RequestUri.GetLeftPart(UriPartial.Path)
            : url;

        if (_responses.TryGetValue(matchUrl, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class MockHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public MockHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
```

- [ ] **Step 2: Verify test project has required packages**

Check that `tests/Asterisk.Platform.Identity.Tests/` has a reference to `Asterisk.Platform.Identity`. If the test project does not exist, it needs to be created. Check with:

```bash
ls tests/Asterisk.Platform.Identity.Tests/
```

If it does not exist, create the test project:

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
dotnet new xunit -o tests/Asterisk.Platform.Identity.Tests --framework net10.0
dotnet sln Asterisk.Platform.slnx add tests/Asterisk.Platform.Identity.Tests/
```

Then add project references and package references as needed:

```xml
<ProjectReference Include="..\..\src\Asterisk.Platform.Identity\Asterisk.Platform.Identity.csproj" />
<PackageReference Include="FluentAssertions" />
<PackageReference Include="System.IdentityModel.Tokens.Jwt" />
<PackageReference Include="Microsoft.IdentityModel.Tokens" />
```

- [ ] **Step 3: Run tests**

```bash
dotnet test tests/Asterisk.Platform.Identity.Tests/ -v q
```

Expected: All 7 tests pass.

- [ ] **Step 4: Commit**

```bash
git add tests/Asterisk.Platform.Identity.Tests/
git commit -m "test(identity): add OidcTokenExchangeService tests — PKCE, token exchange, ID token validation"
```

---

### Task 9: Tests — OidcUserProvisioningService

**Files:**
- Create: `tests/Asterisk.Platform.Identity.Tests/OidcUserProvisioningServiceTests.cs`

- [ ] **Step 1: Create OidcUserProvisioningServiceTests.cs**

Create `tests/Asterisk.Platform.Identity.Tests/OidcUserProvisioningServiceTests.cs`:

```csharp
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity.OidcTokenExchange;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Platform.Identity.Tests;

public sealed class OidcUserProvisioningServiceTests
{
    private readonly InMemoryUserStore _userStore = new();
    private readonly OidcUserProvisioningService _sut;

    public OidcUserProvisioningServiceTests()
    {
        _sut = new OidcUserProvisioningService(
            _userStore,
            NullLogger<OidcUserProvisioningService>.Instance);
    }

    private static TenantAuthConfig DefaultConfig(bool autoCreate = true) => new()
    {
        TenantId = "tenant-1",
        OidcEnabled = true,
        OidcAutoCreateUsers = autoCreate,
        OidcDefaultRole = "Agent",
    };

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldCreateNewUser_WhenAutoCreateEnabled()
    {
        var claims = new OidcClaimsResult("oidc-sub-1", "user@example.com", "Test User", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(), CancellationToken.None);

        user.Should().NotBeNull();
        user!.Email.Should().Be("user@example.com");
        user.DisplayName.Should().Be("Test User");
        user.OidcSubject.Should().Be("oidc-sub-1");
        user.AuthProvider.Should().Be("oidc");
        user.Role.Should().Be(UserRole.Agent);
        user.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldReturnNull_WhenAutoCreateDisabled()
    {
        var claims = new OidcClaimsResult("oidc-sub-1", "user@example.com", "Test User", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(autoCreate: false), CancellationToken.None);

        user.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldReturnExistingUser_WhenOidcSubjectMatches()
    {
        var existing = new User
        {
            UserId = EntityId.NewId(),
            TenantId = new TenantId("tenant-1"),
            Email = "user@example.com",
            DisplayName = "Old Name",
            Role = UserRole.Supervisor,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            AuthProvider = "oidc",
            OidcSubject = "oidc-sub-1",
        };
        await _userStore.SaveAsync(existing, CancellationToken.None);

        var claims = new OidcClaimsResult("oidc-sub-1", "user@example.com", "New Name", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(), CancellationToken.None);

        user.Should().NotBeNull();
        user!.UserId.Should().Be(existing.UserId);
        user.DisplayName.Should().Be("New Name"); // Updated
        user.Role.Should().Be(UserRole.Supervisor); // Preserved, not overwritten
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldLinkByEmail_WhenOidcSubjectNotFoundButEmailMatches()
    {
        var existing = new User
        {
            UserId = EntityId.NewId(),
            TenantId = new TenantId("tenant-1"),
            Email = "user@example.com",
            DisplayName = "Admin User",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            AuthProvider = "local",
        };
        await _userStore.SaveAsync(existing, CancellationToken.None);

        var claims = new OidcClaimsResult("oidc-sub-new", "user@example.com", "Admin User", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(), CancellationToken.None);

        user.Should().NotBeNull();
        user!.UserId.Should().Be(existing.UserId);
        user.OidcSubject.Should().Be("oidc-sub-new"); // Linked
        user.AuthProvider.Should().Be("oidc"); // Updated
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldUseDefaultRole_WhenConfigured()
    {
        var config = new TenantAuthConfig
        {
            TenantId = "tenant-1",
            OidcAutoCreateUsers = true,
            OidcDefaultRole = "Supervisor",
        };

        var claims = new OidcClaimsResult("oidc-sub-1", "supervisor@example.com", "Sup User", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, config, CancellationToken.None);

        user.Should().NotBeNull();
        user!.Role.Should().Be(UserRole.Supervisor);
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldFallbackToAgent_WhenRoleInvalid()
    {
        var config = new TenantAuthConfig
        {
            TenantId = "tenant-1",
            OidcAutoCreateUsers = true,
            OidcDefaultRole = "InvalidRole",
        };

        var claims = new OidcClaimsResult("oidc-sub-1", "agent@example.com", "Agent", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, config, CancellationToken.None);

        user.Should().NotBeNull();
        user!.Role.Should().Be(UserRole.Agent);
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldUseEmail_WhenNameIsNull()
    {
        var claims = new OidcClaimsResult("oidc-sub-1", "user@example.com", null, true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(), CancellationToken.None);

        user.Should().NotBeNull();
        user!.DisplayName.Should().Be("user@example.com");
    }
}
```

Note: This test uses `InMemoryUserStore` directly. Ensure the test project references `Asterisk.Platform.Storage.InMemory`:

```xml
<ProjectReference Include="..\..\src\Asterisk.Platform.Storage.InMemory\Asterisk.Platform.Storage.InMemory.csproj" />
```

And `InternalsVisibleTo` is set in `Asterisk.Platform.Storage.InMemory.csproj` for the test project, or use the public API. Since `InMemoryUserStore` is `internal`, the test project will need:

```xml
<!-- In src/Asterisk.Platform.Storage.InMemory/Asterisk.Platform.Storage.InMemory.csproj -->
<InternalsVisibleTo Include="Asterisk.Platform.Identity.Tests" />
```

- [ ] **Step 2: Run tests**

```bash
dotnet test tests/Asterisk.Platform.Identity.Tests/ -v q
```

Expected: All 14 tests pass (7 exchange + 7 provisioning).

- [ ] **Step 3: Commit**

```bash
git add tests/Asterisk.Platform.Identity.Tests/ \
        src/Asterisk.Platform.Storage.InMemory/Asterisk.Platform.Storage.InMemory.csproj
git commit -m "test(identity): add OidcUserProvisioningService tests — create, update, link, auto-create disabled"
```

---

### Task 10: Full solution build + test verification

- [ ] **Step 1: Build entire solution**

```bash
dotnet build Asterisk.Platform.slnx
```

Expected: Build succeeds, 0 warnings.

- [ ] **Step 2: Run all tests**

```bash
dotnet test Asterisk.Platform.slnx -v q
```

Expected: All tests pass (existing 1,162 + ~14 new OIDC tests = ~1,176 total).

- [ ] **Step 3: Verify 0 warnings**

```bash
dotnet build Asterisk.Platform.slnx 2>&1 | grep -i warning
```

Expected: No output (0 warnings).

- [ ] **Step 4: Update plan file — mark complete**

Update this plan file: mark all tasks as complete.

---

## Summary

| Task | Files | What |
|------|-------|------|
| 1 | AuthEvent.cs, User.cs | OIDC event types + OidcSubject property |
| 2 | IUserStore, InMemory, Postgres, SQL | FindByOidcSubjectAsync + migration |
| 3 | OidcModels.cs | OIDC DTOs + AOT JSON context |
| 4 | IOidcTokenExchangeService, OidcTokenExchangeService | Token exchange + JWKS + PKCE helpers |
| 5 | IOidcUserProvisioningService, OidcUserProvisioningService | Auto-provisioning from OIDC claims |
| 6 | OidcEndpoints.cs | Full rewrite with PKCE + nonce + encrypted cookie |
| 7 | Program.cs, ApiJsonContext.cs | DI wiring + AOT serialization |
| 8 | OidcTokenExchangeServiceTests.cs | 7 tests: PKCE, exchange, validation |
| 9 | OidcUserProvisioningServiceTests.cs | 7 tests: create, update, link, disabled |
| 10 | Full solution | Build + test verification |

**Estimated new tests:** ~14
**Estimated new files:** 6 (3 interfaces/implementations, 1 model file, 2 test files)
**Estimated modified files:** 8 (User, IUserStore, InMemoryUserStore, PostgresUserStore, OidcEndpoints, Program.cs, ApiJsonContext, AuthEvent, SQL migrations)
**Commits:** 8
