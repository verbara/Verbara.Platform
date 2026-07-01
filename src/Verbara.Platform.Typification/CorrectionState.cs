namespace Verbara.Platform.Typification;

/// <summary>
/// Append-only correction state of a <see cref="TypificationSubmission"/> (ADR-0034 Decision 5).
/// An autonomously stamped disposition stays immutable; a supervisor correction creates a NEW
/// corrective submission that references the original — the original is then marked
/// <see cref="Corrected"/>. The conversation is NOT transitioned back to WrapUp.
/// </summary>
public enum CorrectionState
{
    /// <summary>Not corrected (the default for every submission).</summary>
    None = 0,

    /// <summary>This submission has been superseded by a later corrective submission.</summary>
    Corrected = 1,
}
