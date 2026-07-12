using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The identity fields a verified WebChat CSAT capture token binds. Returned by
/// <see cref="ICsatWebChatTokenVerifier.Verify"/> on a valid, unexpired,
/// signature-checked <c>responseToken</c>.
/// </summary>
/// <param name="TenantId">The tenant the capture belongs to.</param>
/// <param name="SurveyId">The survey the rating belongs to (fixture <c>svi</c>).</param>
/// <param name="QueueName">The queue the conversation was handled on (fixture <c>q</c>).</param>
/// <param name="Channel">The signed capture channel (fixture <c>ch</c>).</param>
/// <param name="IssuedAt">The instant the token was issued (TTL is measured from here).</param>
internal sealed record CsatWebChatToken(
    string TenantId,
    string SurveyId,
    string QueueName,
    string Channel,
    DateTimeOffset IssuedAt);

/// <summary>
/// Verifies the Platform-minted, session-signed WebChat CSAT <c>responseToken</c>
/// (csat-runner Phase B, spec "Token-signed WebChat CSAT capture wire shape").
/// The webchat token is Platform-minted (embed iframe holds it); the email reply
/// token is Pro-minted (<c>Verbara.Sdk.Pro.CsatRunner.Tokens.ICsatReplyTokenSigner</c>)
/// and handled by the IMAP gap-fill in Phase C — NOT here.
/// </summary>
internal interface ICsatWebChatTokenVerifier
{
    /// <summary>
    /// Verifies the token's structure, HMAC-SHA256 signature, and TTL and, on success,
    /// returns the bound claims. Returns <see langword="null"/> when the token is missing,
    /// malformed, its signature does not verify (constant-time compare), or it has expired
    /// relative to <paramref name="now"/>.
    /// </summary>
    CsatWebChatToken? Verify(string? token, DateTimeOffset now);
}

/// <summary>
/// AOT-safe HMAC-SHA256 verifier for the versioned <c>v1.{payload}.{sig}</c> WebChat CSAT
/// token. <c>payload</c> is the base64url (no padding) encoding of a compact JSON object
/// <c>{ tid, svi, q, ch, iat }</c> and <c>sig</c> is the base64url HMAC-SHA256 of the UTF-8
/// <c>"v1.{payload}"</c> prefix under the configured secret. Mirrors the design of Pro's
/// <c>HmacCsatReplyTokenSigner</c> (no reflection, <see cref="HMACSHA256"/> only) but binds
/// the <c>(tenant, survey, queue, channel)</c> claims the capture endpoint reconciles against
/// the submitted <c>queueName</c>/<c>channel</c>.
/// </summary>
internal sealed class HmacCsatWebChatTokenVerifier : ICsatWebChatTokenVerifier
{
    private const string Version = "v1";
    private static readonly TimeSpan s_ttl = TimeSpan.FromDays(7);

    private readonly byte[] _secret;

    public HmacCsatWebChatTokenVerifier(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        _secret = secret;
    }

    public CsatWebChatToken? Verify(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        // Structure: exactly "{version}.{payload}.{sig}".
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Version)
            return null;

        var signedPrefix = $"{parts[0]}.{parts[1]}";

        if (!TryDecodeBase64Url(parts[2], out var providedSig))
            return null;

        Span<byte> expectedSig = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(signedPrefix), expectedSig);

        // Constant-time compare — reject tampered signatures without a timing side-channel.
        if (providedSig.Length != expectedSig.Length ||
            !CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
            return null;

        if (!TryDecodeBase64Url(parts[1], out var payloadBytes))
            return null;

        CsatWebChatTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(payloadBytes, CsatTokenJsonContext.Default.CsatWebChatTokenPayload);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null ||
            string.IsNullOrEmpty(payload.TenantId) ||
            string.IsNullOrEmpty(payload.SurveyId) ||
            string.IsNullOrEmpty(payload.QueueName) ||
            string.IsNullOrEmpty(payload.Channel))
            return null;

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAtUnixSeconds);

        // TTL: reject expired tokens (issued more than the TTL before now). Tokens issued in
        // the far future are also rejected as malformed clock-skew abuse.
        if (now - issuedAt > s_ttl || issuedAt - now > TimeSpan.FromMinutes(5))
            return null;

        return new CsatWebChatToken(
            payload.TenantId,
            payload.SurveyId,
            payload.QueueName,
            payload.Channel,
            issuedAt);
    }

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        var maxLen = Base64Url.GetMaxDecodedLength(value.Length);
        var buffer = new byte[maxLen];
        if (Base64Url.DecodeFromChars(value, buffer, out _, out var written) != OperationStatus.Done)
        {
            bytes = [];
            return false;
        }

        bytes = written == buffer.Length ? buffer : buffer[..written];
        return true;
    }
}

/// <summary>
/// The compact JSON payload carried in the WebChat CSAT token's middle segment
/// (<c>{ tid, svi, q, ch, iat }</c>). Serialized via source-gen (Native AOT, no reflection).
/// </summary>
internal sealed record CsatWebChatTokenPayload
{
    [JsonPropertyName("tid")] public string TenantId { get; init; } = "";
    [JsonPropertyName("svi")] public string SurveyId { get; init; } = "";
    [JsonPropertyName("q")] public string QueueName { get; init; } = "";
    [JsonPropertyName("ch")] public string Channel { get; init; } = "";
    [JsonPropertyName("iat")] public long IssuedAtUnixSeconds { get; init; }
}

[JsonSerializable(typeof(CsatWebChatTokenPayload))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
internal sealed partial class CsatTokenJsonContext : JsonSerializerContext;
