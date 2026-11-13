using System.Text.Json.Serialization;

namespace Verbara.Platform.Api.Endpoints;

// qa-scores-are-produced-or-not-shown (design D1): the body of the QA availability read. Every nullable member is
// always written as an explicit null (JsonIgnoreCondition.Never), because both of the host's serializers omit nulls
// and the console reads a missing member and a null one differently. Registered in ApiJsonContext.

/// <summary>
/// Whether post-call analysis produces results for the caller's tenant. <see cref="AnalysisRunning"/> is true when the
/// analysis is registered, licensed and has a transcript source; <see cref="Available"/> (QA scores) additionally needs a
/// scorecard with at least one item for the tenant. <see cref="Reason"/> is the first missing piece, null when
/// <see cref="Available"/> is true. <see cref="SpeakerMode"/>, <see cref="TopicsConfigured"/>,
/// <see cref="ComplianceConfigured"/> and <see cref="Backlog"/> are null when <see cref="AnalysisRunning"/> is false.
/// </summary>
internal sealed record QaAvailabilityDto(
    bool Available,
    bool AnalysisRunning,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TranscriptSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SpeakerMode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? TopicsConfigured,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? ComplianceConfigured,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] QaBacklogDto? Backlog)
{
    /// <summary>No analytics connection string, so post-call analysis is not part of this host.</summary>
    public const string ReasonAnalysisNotRegistered = "analysis_not_registered";

    /// <summary>The licence does not grant call analytics.</summary>
    public const string ReasonNotLicensed = "not_licensed";

    /// <summary>No transcript source is configured, so no call is ever analysed.</summary>
    public const string ReasonNoTranscriptSource = "no_transcript_source";

    /// <summary>No queue of the caller's tenant resolves to a scorecard with at least one item.</summary>
    public const string ReasonNoScorecard = "no_scorecard";

    /// <summary>Transcripts come from the call's recording.</summary>
    public const string TranscriptSourceRecording = "recording";

    /// <summary>One recording per direction: results can name the speaker.</summary>
    public const string SpeakerModeTwoTrack = "two_track";

    /// <summary>One mixed recording: speaker-dependent results are not verified.</summary>
    public const string SpeakerModeMono = "mono";
}

/// <summary>
/// The analysis backlog of the caller's tenant since <see cref="Since"/>: calls waiting for analysis, and calls that
/// ended without being analysed.
/// </summary>
internal sealed record QaBacklogDto(long Waiting, long NotAnalysed, DateTimeOffset Since);
