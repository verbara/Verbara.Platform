namespace Verbara.Platform.Typification;

/// <summary>
/// Reference to a field pre-fill source (P2/P3): a metadata key, an AI entity
/// name, or a data-dip reference. Unused in P0 logic but must exist and be
/// JSON-serializable.
/// </summary>
public sealed record PrefillRef
{
    public required PrefillSourceKind Kind { get; init; }

    /// <summary>Metadata key, AI entity name, or data-dip id depending on <see cref="Kind"/>.</summary>
    public required string Ref { get; init; }
}
