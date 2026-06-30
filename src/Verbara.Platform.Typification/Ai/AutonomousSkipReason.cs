namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Why <see cref="IAutonomousDispositionPolicy"/> declined to autonomously stamp a disposition
/// on an abandoned wrap-up close (ADR-0034). Each value names exactly one gate that failed; the
/// policy returns the <b>first</b> failing gate's reason (see the evaluation order in
/// <see cref="DefaultAutonomousDispositionPolicy"/>). Emitted as the <c>reason</c> dimension on the
/// <c>AutonomousSkipped</c> audit event / metric by the close-path consumer (task group 4).
/// </summary>
public enum AutonomousSkipReason
{
    /// <summary>No pending AI suggestion exists for the conversation.</summary>
    NoSuggestion,

    /// <summary>A suggestion exists but its confidence is below the schema's
    /// <see cref="TypificationAiConfig.AutonomousThreshold"/>.</summary>
    BelowThreshold,

    /// <summary>The deployment does not hold both required Pro <c>LicenseFeature</c> entitlements
    /// (<c>AdvancedTypification</c> AND <c>TypificationAi</c>).</summary>
    LicenseMissing,

    /// <summary>The tenant has no active autonomous-disposition activation gate (never recorded,
    /// or revoked).</summary>
    GateDisabled,

    /// <summary>The suggested leaf node is no longer a valid leaf in the active published schema
    /// (the taxonomy mutated between suggestion and evaluation).</summary>
    SchemaMutation,

    /// <summary>The conversation is no longer in the <c>WrapUp</c> state (e.g. a human already
    /// moved it).</summary>
    NotWrapUp,
}
