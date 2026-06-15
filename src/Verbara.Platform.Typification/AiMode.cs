using System.Text.Json.Serialization;

namespace Verbara.Platform.Typification;

/// <summary>Graduated AI automation band for a typification schema (P2b).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiMode>))]
public enum AiMode
{
    /// <summary>AI is switched off; no classification is attempted.</summary>
    Off = 0,

    /// <summary>
    /// Shadow mode — the classifier runs and results are logged for accuracy
    /// measurement but nothing is surfaced to the agent.
    /// </summary>
    Shadow = 1,

    /// <summary>Pre-fill the suggestion above <c>SuggestThreshold</c>; the agent must confirm.</summary>
    SuggestOnly = 2,

    /// <summary>
    /// Auto-apply the suggestion when confidence is at or above
    /// <c>AutoApplyThreshold</c>; falls back to suggest for the band below.
    /// </summary>
    AutoFill = 3,
}
