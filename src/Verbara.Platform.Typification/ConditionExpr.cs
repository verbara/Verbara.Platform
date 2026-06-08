namespace Verbara.Platform.Typification;

/// <summary>
/// A single show/hide rule for a field ("respuestas entrelazadas").
/// P0 = single condition; AND/OR groups are a later phase (P4).
/// </summary>
public sealed record ConditionExpr
{
    /// <summary>What <see cref="Ref"/> points at (a field Key or a node Code).</summary>
    public required ConditionRef RefType { get; init; }

    /// <summary>The referenced field <c>Key</c> or node <c>Code</c>.</summary>
    public required string Ref { get; init; }

    public required ConditionOp Op { get; init; }

    /// <summary>The compared value (CSV for <see cref="ConditionOp.In"/>).</summary>
    public string? Value { get; init; }
}
