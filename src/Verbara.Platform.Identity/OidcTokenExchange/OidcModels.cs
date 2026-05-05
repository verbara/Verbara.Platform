using System.Text.Json.Serialization;

namespace Verbara.Platform.Identity.OidcTokenExchange;

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
