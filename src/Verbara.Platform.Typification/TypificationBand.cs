using System.Text.Json.Serialization;

namespace Verbara.Platform.Typification;

/// <summary>
/// Server-authoritative delivery band for an AI typification suggestion (C1, P2b).
/// The client MUST NOT escalate the band — the server decides.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TypificationBand>))]
public enum TypificationBand
{
    /// <summary>No suggestion surfaced (AI off, shadow, below threshold, sentiment-gated).</summary>
    None = 0,

    /// <summary>Suggestion shown as a banner; the agent confirms before applying.</summary>
    Suggest = 1,

    /// <summary>Form pre-filled automatically; mode is <see cref="AiMode.AutoFill"/> and calibration is ready.</summary>
    AutoFill = 2,
}
