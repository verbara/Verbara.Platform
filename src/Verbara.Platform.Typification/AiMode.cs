namespace Verbara.Platform.Typification;

/// <summary>How AI suggestions are applied to a typification form (P2).</summary>
public enum AiMode
{
    /// <summary>Pre-fill the suggestion; the agent must confirm.</summary>
    SuggestOnly = 0,

    /// <summary>Auto-apply when confidence is at or above the configured threshold.</summary>
    AutoApplyAboveThreshold = 1,
}
