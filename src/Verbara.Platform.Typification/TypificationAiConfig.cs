using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Typification;

/// <summary>
/// Graduated AI automation configuration for a typification schema (P2b).
/// </summary>
/// <remarks>
/// Confidence band ordering invariant: <c>SuggestThreshold</c> ≤
/// <c>AutoApplyThreshold</c> ≤ <c>AutonomousThreshold</c>.
/// </remarks>
public sealed record TypificationAiConfig
{
    /// <summary>Whether AI classification is active for this schema.</summary>
    public bool Enabled { get; init; }

    /// <summary>Automation band (Off / Shadow / SuggestOnly / AutoFill).</summary>
    public AiMode Mode { get; init; } = AiMode.Off;

    /// <summary>
    /// Minimum confidence to surface a suggestion to the agent
    /// (<see cref="AiMode.SuggestOnly"/> and above).  Default 0.6.
    /// </summary>
    public double SuggestThreshold { get; init; } = 0.6;

    /// <summary>
    /// Minimum confidence for auto-application without agent confirmation
    /// (<see cref="AiMode.AutoFill"/>).  Default 0.85.
    /// </summary>
    public double AutoApplyThreshold { get; init; } = 0.85;

    /// <summary>
    /// Minimum confidence for fully autonomous disposition (no agent review
    /// — reserved for <c>Autonomous = true</c>).  Default 0.95.
    /// </summary>
    public double AutonomousThreshold { get; init; } = 0.95;

    /// <summary>
    /// When <see langword="true"/> the classifier may close the conversation without
    /// any agent review, provided confidence ≥ <see cref="AutonomousThreshold"/>.
    /// Default <see langword="false"/>.
    /// </summary>
    public bool Autonomous { get; init; }

    /// <summary>Never auto-pick a Success leaf when sentiment is VeryNegative.</summary>
    public bool SentimentGating { get; init; } = true;

    /// <summary>
    /// Maximum LLM tokens that may be consumed by AI classification for this schema
    /// in a rolling 24-hour window.  <see langword="null"/> = no budget cap.
    /// </summary>
    public long? DailyTokenBudget { get; init; }

    /// <summary>AI entity name → field <c>Key</c>.</summary>
    public required IReadOnlyDictionary<string, string> EntityFieldMap { get; init; }

    /// <summary>
    /// PII allow-list applied to AI-extracted field values before they are surfaced/stored.
    /// Default <see cref="PiiPolicy.DenyAll"/> = mask all detected PII (card/national-id/phone/email);
    /// admins opt specific types in via the schema designer (D4). Screening is also re-applied on the
    /// write path (D3) as defense-in-depth.
    /// </summary>
    public PiiPolicy PiiPolicy { get; init; } = PiiPolicy.DenyAll;
}
