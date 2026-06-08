namespace Verbara.Platform.Typification;

/// <summary>Server-authoritative validation rules for a typification field.</summary>
public sealed record FieldValidation
{
    public string? Regex { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public int? MaxLength { get; init; }
}
