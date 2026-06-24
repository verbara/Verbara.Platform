namespace Verbara.Platform.Billing;

/// <summary>
/// Measurement unit for a usage record.
/// </summary>
public enum UsageUnit
{
    Minutes,
    Segments,
    Conversations,
    Bytes,
    Count,
    Hours,
    /// <summary>Raw LLM tokens (technical unit). Commercialized as AI Credits via PlatformLlmOptions.CreditTokenRatio.</summary>
    Tokens = 6,
}
