namespace Verbara.Platform.Typification;

/// <summary>Origin of a field pre-fill value (P2/P3).</summary>
public enum PrefillSourceKind
{
    /// <summary>A conversation metadata key.</summary>
    Metadata = 0,

    /// <summary>A named AI-extracted entity.</summary>
    AiEntity = 1,

    /// <summary>A data-dip reference.</summary>
    DataDip = 2,
}
