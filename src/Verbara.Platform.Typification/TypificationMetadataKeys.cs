namespace Verbara.Platform.Typification;

/// <summary>
/// Well-known <c>Conversation.Metadata</c> keys used by the typification capture
/// pipeline, shared by all P1 capture writers and the prefill consumer.
/// </summary>
public static class TypificationMetadataKeys
{
    /// <summary>
    /// The captured reason taxonomy path, stored as a JSON array of node Codes
    /// (e.g. <c>["CITAS","REPROG","GINE"]</c>).
    /// </summary>
    public const string ReasonPath = "reasonPath";
}
