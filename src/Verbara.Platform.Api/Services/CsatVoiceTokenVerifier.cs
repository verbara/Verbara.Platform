using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The identity fields a verified voice-leg CSAT capture token binds. Returned by
/// <see cref="ICsatVoiceTokenVerifier.Verify"/> on a valid, unexpired, signature-checked
/// <c>responseToken</c> (csat-completion, Platform/ADR-0020).
/// </summary>
/// <param name="TenantId">The tenant the capture belongs to (fixture <c>tid</c>).</param>
/// <param name="SurveyId">The survey the rating belongs to (fixture <c>svi</c>).</param>
/// <param name="QueueName">The queue the conversation was handled on (fixture <c>q</c>).</param>
/// <param name="Channel">The signed capture channel (fixture <c>ch</c>) — <c>voice</c>.</param>
/// <param name="IssuedAt">The instant the token was issued (TTL is measured from here).</param>
internal sealed record CsatVoiceToken(
    string TenantId,
    string SurveyId,
    string QueueName,
    string Channel,
    DateTimeOffset IssuedAt);

/// <summary>
/// Verifies the Platform-minted voice-leg CSAT <c>responseToken</c> the survey-IVR leg submits with
/// its DTMF rating (csat-completion, Platform/ADR-0020, spec "Voice CSAT capture wire shape"). The
/// voice token is Platform-minted (set on the caller leg as the <c>SURVEY_TOKEN</c> channel variable at
/// the survey-IVR handoff) and mirrors the webchat token's <c>v1.{payload}.{sig}</c> HMAC pattern
/// (<see cref="ICsatWebChatTokenVerifier"/>) — the same
/// <c>{ tid, svi, q, ch, iat }</c> claim set, with <c>ch</c> = <c>voice</c>.
/// </summary>
internal interface ICsatVoiceTokenVerifier
{
    /// <summary>
    /// Verifies the token's structure, HMAC-SHA256 signature, and TTL and, on success, returns the
    /// bound claims. Returns <see langword="null"/> when the token is missing, malformed, its signature
    /// does not verify (constant-time compare), or it has expired relative to <paramref name="now"/>.
    /// </summary>
    CsatVoiceToken? Verify(string? token, DateTimeOffset now);
}

/// <summary>
/// AOT-safe HMAC-SHA256 verifier for the versioned <c>v1.{payload}.{sig}</c> voice-leg CSAT token.
/// <c>payload</c> is the base64url (no padding) encoding of a compact JSON object
/// <c>{ tid, svi, q, ch, iat }</c> and <c>sig</c> is the base64url HMAC-SHA256 of the UTF-8
/// <c>"v1.{payload}"</c> prefix under the configured secret. Mirrors
/// <see cref="HmacCsatWebChatTokenVerifier"/> (no reflection, <see cref="HMACSHA256"/> only) and binds
/// the <c>(tenant, survey, queue, channel)</c> claims the voice capture endpoint reconciles against the
/// submitted <c>queueName</c>/<c>channel</c>.
/// </summary>
internal sealed class HmacCsatVoiceTokenVerifier : ICsatVoiceTokenVerifier
{
    private const string Version = "v1";
    private static readonly TimeSpan s_ttl = TimeSpan.FromDays(7);

    private readonly byte[] _secret;

    public HmacCsatVoiceTokenVerifier(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        _secret = secret;
    }

    public CsatVoiceToken? Verify(string? token, DateTimeOffset now)
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

        return new CsatVoiceToken(
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
