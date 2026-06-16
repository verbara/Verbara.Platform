namespace Verbara.Platform.Typification;

/// <summary>
/// Shared <see cref="FieldType"/> classification helpers — the single source of truth so the
/// validator (length cap) and the endpoint (PII screen) can never diverge on which field types
/// are "free text" (a future field type can't be screened-but-not-capped, or vice-versa).
/// </summary>
public static class FieldTypeExtensions
{
    /// <summary>
    /// True for the free-text field types (Text / Textarea / Lookup). These are the ONLY types
    /// subject to the AI implicit length cap (D3) AND the AI-write-path PII screen — a typed
    /// field such as <see cref="FieldType.Phone"/> legitimately holds a phone number and must
    /// not be masked or cap-clamped.
    /// </summary>
    public static bool IsFreeText(this FieldType type) =>
        type is FieldType.Text or FieldType.Textarea or FieldType.Lookup;
}
