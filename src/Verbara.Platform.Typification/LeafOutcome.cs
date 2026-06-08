namespace Verbara.Platform.Typification;

/// <summary>
/// Outcome semantics for a leaf node (the old flat Disposition/DispositionCode
/// collapse here). Non-null iff the owning <see cref="TypificationNode"/> is a leaf.
/// </summary>
public sealed record LeafOutcome
{
    public required TypificationCategory Category { get; init; }
    public bool TriggerRetry { get; init; }
    public int? RetryDelayMinutes { get; init; }
    public bool TriggerCallback { get; init; }

    /// <summary>Optional bridge to a Pro campaign <c>DispositionCode</c> (outbound).</summary>
    public string? DialerCode { get; init; }

    public bool IsActive { get; init; } = true;
}
