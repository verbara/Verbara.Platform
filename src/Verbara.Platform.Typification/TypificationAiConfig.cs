namespace Verbara.Platform.Typification;

/// <summary>
/// AI auto-suggest / pre-fill configuration for a schema (P2 — disabled in P0).
/// </summary>
public sealed record TypificationAiConfig
{
    public bool Enabled { get; init; }

    public AiMode Mode { get; init; }

    public double ConfidenceThreshold { get; init; }

    /// <summary>Never auto-pick a Success leaf when sentiment is VeryNegative.</summary>
    public bool SentimentGating { get; init; }

    /// <summary>AI entity name → field <c>Key</c>.</summary>
    public required IReadOnlyDictionary<string, string> EntityFieldMap { get; init; }
}
