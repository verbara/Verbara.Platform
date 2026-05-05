using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Identity.OidcTokenExchange;

public sealed partial class OidcTokenExchangeService : IOidcTokenExchangeService
{
    /// <summary>
    /// Keyed-service name for the transient-retry <see cref="ResiliencePolicy"/> that wraps
    /// the OIDC token-endpoint POST. Discovery + JWKS fetches are cached for 24h and are
    /// not wrapped. JWT validation is deterministic and never retried.
    /// </summary>
    public const string ResiliencePolicyKey = "oidc.token-exchange";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<OidcTokenExchangeService> _logger;
    private readonly ConcurrentDictionary<string, CachedDiscovery> _discoveryCache = new();
    private readonly ConcurrentDictionary<string, CachedJwks> _jwksCache = new();
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan JwksCacheTtl = TimeSpan.FromHours(24);

    public OidcTokenExchangeService(
        IHttpClientFactory httpClientFactory,
        ILogger<OidcTokenExchangeService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public async Task<OidcTokenResponse> ExchangeCodeAsync(
        string authority, string code, string codeVerifier,
        string redirectUri, string clientId, string clientSecret,
        CancellationToken ct)
    {
        var discovery = await GetDiscoveryAsync(authority, ct);

        var client = _httpClientFactory.CreateClient("oidc");

        // Transient-retry wraps ONLY the HTTP POST; JWT deserialization + validation below
        // is deterministic and must not be retried on parse failure.
        var response = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri,
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["code_verifier"] = codeVerifier,
                });
                return await client.PostAsync(discovery.TokenEndpoint, content, innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
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
        string idToken, string authority, string expectedAudience,
        string expectedNonce, CancellationToken ct)
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
            LogJwksKeyNotFound(_logger, authority);
            jwks = await GetJwksAsync(discovery.JwksUri, authority, forceRefresh: true, ct);
            validationParameters.IssuerSigningKeys = jwks;
            principal = handler.ValidateToken(idToken, validationParameters, out _);
        }

        var nonceClaim = principal.FindFirst("nonce")?.Value;
        if (!string.Equals(nonceClaim, expectedNonce, StringComparison.Ordinal))
            throw new SecurityTokenValidationException(
                "ID token nonce does not match expected value — possible replay attack");

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

    public static string GenerateCodeVerifier() =>
        GenerateRandomBase64Url(32);

    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    public static string GenerateNonce() =>
        GenerateRandomBase64Url(32);

    private static string GenerateRandomBase64Url(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Base64UrlEncode(bytes);
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "JWKS key not found for authority {Authority}, forcing refresh")]
    private static partial void LogJwksKeyNotFound(ILogger logger, string authority);

    private sealed record CachedDiscovery(OidcDiscoveryDocument Document, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }

    private sealed record CachedJwks(IReadOnlyList<SecurityKey> Keys, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
