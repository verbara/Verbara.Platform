namespace Verbara.Platform.Typification;

/// <summary>
/// Maps a JSON path from a data-dip response onto a target field — either as the
/// field's value or as its dynamic options source (P3).
/// </summary>
public sealed record ResponseMapping
{
    public required string JsonPath { get; init; }
    public required string TargetFieldKey { get; init; }

    /// <summary>True when the mapped value populates the field's options instead of its value.</summary>
    public bool IsOptionsSource { get; init; }
}
