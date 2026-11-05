using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Verbara.Platform.Channels.Core;

namespace Verbara.Platform.Api.Services;

/// <summary>What a verified identity token keeps. Every other claim is dropped.</summary>
/// <param name="ExternalId">The site's own id of its logged-in customer, verbatim.</param>
/// <param name="Name">The customer's display name, when the site sent one.</param>
/// <param name="Email">The customer's e-mail, when the site sent one. Display data: it never links contacts.</param>
internal sealed record WebChatIdentityClaims(string ExternalId, string? Name, string? Email);

/// <summary>Either the claims of a verified token or the code it was refused with.</summary>
internal readonly record struct WebChatIdentityTokenResult(WebChatIdentityClaims? Claims, string? RefusalCode);

/// <summary>The codes a session response names when it does not honour a token.</summary>
internal static class WebChatIdentityTokenRefusal
{
    public const string NotConfigured = "identity-token-not-configured";
    public const string Malformed = "identity-token-malformed";
    public const string SignatureInvalid = "identity-token-signature-invalid";
    public const string WrongTenant = "identity-token-wrong-tenant";
    public const string LifetimeTooLong = "identity-token-lifetime-too-long";
    public const string NotYetValid = "identity-token-not-yet-valid";
    public const string Expired = "identity-token-expired";
}

/// <summary>
/// Verifies the JWT a tenant's site signs for its logged-in WebChat visitor: HS256 only, keyed with the tenant's own
/// secret, bound to the tenant by <c>aud</c>, valid for at most an hour with five minutes of skew. Hand-written with
/// source-generated JSON, because general-purpose JWT handlers serialize by reflection and the Api is Native AOT.
/// The signature is checked before any claim is trusted, so a clock or tenant code is only ever reported for a token
/// the tenant's own secret signed. A token, its signature and its claims are never logged: a refusal logs the tenant
/// and the code.
/// </summary>
internal sealed partial class WebChatIdentityTokenVerifier(ILogger<WebChatIdentityTokenVerifier> logger)
{
    private const int MaxTokenLength = 4096;
    private const int MaxExternalIdLength = 256;
    private const int MaxNameLength = 200;
    private const int MaxEmailLength = 320;
    private const int MaxLifetimeSeconds = 3600;
    private const int SkewSeconds = 300;
    private const string Algorithm = "HS256";
    private const string TokenType = "JWT";

    private static readonly TimeSpan s_rotationGrace = TimeSpan.FromHours(24);

    private static readonly string[] s_instantFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    /// <summary>
    /// Verifies <paramref name="token"/> for <paramref name="tenantId"/> against that tenant's WebChat credentials.
    /// </summary>
    public WebChatIdentityTokenResult Verify(
        string token, string tenantId, IReadOnlyDictionary<string, string>? credentials, DateTimeOffset now)
    {
        var refusal = Evaluate(token, tenantId, credentials, now, out var claims);
        if (refusal is null)
            return new WebChatIdentityTokenResult(claims, null);

        LogRefused(logger, tenantId, refusal);
        return new WebChatIdentityTokenResult(null, refusal);
    }

    // The order of the checks is the contract: each refusal code means every earlier check passed.
    private static string? Evaluate(
        string token,
        string tenantId,
        IReadOnlyDictionary<string, string>? credentials,
        DateTimeOffset now,
        out WebChatIdentityClaims? claims)
    {
        claims = null;
        if (credentials is null
            || !credentials.TryGetValue(ChannelCredentialSchemas.IdentityTokenSecretKey, out var secret)
            || string.IsNullOrWhiteSpace(secret))
        {
            return WebChatIdentityTokenRefusal.NotConfigured;
        }

        if (token.Length > MaxTokenLength)
            return WebChatIdentityTokenRefusal.Malformed;

        var segments = token.Split('.');
        if (segments.Length != 3
            || !TryDecodeSegment(segments[0], out var headerBytes)
            || !TryDecodeSegment(segments[1], out var payloadBytes)
            || !TryDecodeSegment(segments[2], out var signature)
            || !IsAcceptedHeader(headerBytes))
        {
            return WebChatIdentityTokenRefusal.Malformed;
        }

        if (!IsSignedByTenant(segments[0], segments[1], signature, secret, credentials, now))
            return WebChatIdentityTokenRefusal.SignatureInvalid;

        if (!TryReadPayload(payloadBytes, out var payload))
            return WebChatIdentityTokenRefusal.Malformed;

        if (!IsForTenant(payload.Audience!.Value, tenantId))
            return WebChatIdentityTokenRefusal.WrongTenant;

        // Int128: the claims are any 64-bit integers, and a difference or a skew must not wrap.
        Int128 issuedAt = payload.IssuedAt!.Value;
        Int128 expiresAt = payload.ExpiresAt!.Value;
        Int128 nowSeconds = now.ToUnixTimeSeconds();
        if (expiresAt <= issuedAt || expiresAt - issuedAt > MaxLifetimeSeconds)
            return WebChatIdentityTokenRefusal.LifetimeTooLong;

        if (issuedAt > nowSeconds + SkewSeconds)
            return WebChatIdentityTokenRefusal.NotYetValid;

        if (nowSeconds > expiresAt + SkewSeconds)
            return WebChatIdentityTokenRefusal.Expired;

        claims = new WebChatIdentityClaims(payload.ExternalId!, payload.Name, payload.Email);
        return null;
    }

    private static bool IsAcceptedHeader(byte[] headerBytes)
    {
        try
        {
            var header = JsonSerializer.Deserialize(headerBytes, WebChatIdentityTokenJsonContext.Default.WebChatIdentityTokenHeader);
            return header is { Algorithm: Algorithm, Type: null or TokenType };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSignedByTenant(
        string header,
        string payload,
        byte[] signature,
        string secret,
        IReadOnlyDictionary<string, string> credentials,
        DateTimeOffset now)
    {
        var signed = Encoding.ASCII.GetBytes($"{header}.{payload}");
        if (Matches(secret, signed, signature))
            return true;

        return credentials.TryGetValue(ChannelCredentialSchemas.IdentityTokenSecretPreviousKey, out var previous)
               && !string.IsNullOrWhiteSpace(previous)
               && IsInsideRotationGrace(credentials, now)
               && Matches(previous, signed, signature);
    }

    private static bool Matches(string secret, byte[] signed, byte[] signature)
    {
        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed, expected);
        return CryptographicOperations.FixedTimeEquals(signature, expected);
    }

    // Fails closed: a previous secret with a missing or unparseable stamp is never tried.
    private static bool IsInsideRotationGrace(IReadOnlyDictionary<string, string> credentials, DateTimeOffset now) =>
        credentials.TryGetValue(ChannelCredentialSchemas.IdentityTokenSecretReplacedAtKey, out var stamp)
        && DateTimeOffset.TryParseExact(
            stamp, s_instantFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var replacedAt)
        && now < replacedAt + s_rotationGrace;

    private static bool TryReadPayload(byte[] payloadBytes, out WebChatIdentityTokenPayload payload)
    {
        payload = null!;
        WebChatIdentityTokenPayload? read;
        try
        {
            read = JsonSerializer.Deserialize(payloadBytes, WebChatIdentityTokenJsonContext.Default.WebChatIdentityTokenPayload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (read is not { ExternalId: { } externalId, Audience: { } audience, IssuedAt: not null, ExpiresAt: not null }
            || externalId.AsSpan().Trim().Length is 0 or > MaxExternalIdLength
            || externalId.Contains('\0', StringComparison.Ordinal)
            || read.Name is { Length: > MaxNameLength }
            || read.Email is { Length: > MaxEmailLength }
            || !IsWellTypedAudience(audience))
        {
            return false;
        }

        payload = read;
        return true;
    }

    private static bool IsWellTypedAudience(JsonElement audience) => audience.ValueKind switch
    {
        JsonValueKind.String => true,
        JsonValueKind.Array => audience.GetArrayLength() > 0
                               && audience.EnumerateArray().All(member => member.ValueKind == JsonValueKind.String),
        _ => false,
    };

    private static bool IsForTenant(JsonElement audience, string tenantId) =>
        audience.ValueKind == JsonValueKind.String
            ? audience.ValueEquals(tenantId)
            : audience.EnumerateArray().All(member => member.ValueEquals(tenantId));

    // Only the base64url alphabet: the decoder alone would also skip whitespace and accept padding.
    private static bool TryDecodeSegment(string segment, out byte[] bytes)
    {
        bytes = [];
        if (segment.Length == 0)
            return false;

        foreach (var c in segment)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }

        var buffer = new byte[Base64Url.GetMaxDecodedLength(segment.Length)];
        if (Base64Url.DecodeFromChars(segment, buffer, out _, out var written) != OperationStatus.Done)
            return false;

        bytes = written == buffer.Length ? buffer : buffer[..written];
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WebChat identity token refused for tenant {TenantId}: {RefusalCode}")]
    private static partial void LogRefused(ILogger logger, string tenantId, string refusalCode);
}

/// <summary>The token's header. <c>kid</c> is ignored: rotation works by trying the two secrets.</summary>
internal sealed record WebChatIdentityTokenHeader
{
    [JsonPropertyName("alg")] public string? Algorithm { get; init; }
    [JsonPropertyName("typ")] public string? Type { get; init; }
}

/// <summary>The token's claims. Unknown claims are ignored; a claim of the wrong JSON type fails the read.</summary>
internal sealed record WebChatIdentityTokenPayload
{
    [JsonPropertyName("external_id")] public string? ExternalId { get; init; }
    [JsonPropertyName("aud")] public JsonElement? Audience { get; init; }
    [JsonPropertyName("iat")] public long? IssuedAt { get; init; }
    [JsonPropertyName("exp")] public long? ExpiresAt { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
}

[JsonSerializable(typeof(WebChatIdentityTokenHeader))]
[JsonSerializable(typeof(WebChatIdentityTokenPayload))]
internal sealed partial class WebChatIdentityTokenJsonContext : JsonSerializerContext;
