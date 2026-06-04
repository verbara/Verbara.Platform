namespace Verbara.Platform.Api.Voice;

/// <summary>
/// Codec catalog returned by <c>GET /api/v1/admin/voice/codecs</c>.
/// <paramref name="Source"/> is <c>"asterisk"</c> when the list came from a live <c>core show codecs</c>
/// query, or <c>"fallback"</c> when Asterisk could not be reached (static catalog).
/// </summary>
internal sealed record VoiceCodecsResponse(string Source, string[] Codecs);

/// <summary>400 body returned when a trunk/profile write contains unrecognised codec tokens.</summary>
internal sealed record CodecValidationError(string[] InvalidCodecs);
