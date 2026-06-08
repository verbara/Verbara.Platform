namespace Verbara.Platform.Typification;

/// <summary>A static option for a Select/MultiSelect field.</summary>
public sealed record FieldOption
{
    public required string Value { get; init; }
    public required string Label { get; init; }
}
