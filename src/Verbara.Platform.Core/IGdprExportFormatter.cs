namespace Verbara.Platform.Core;

/// <summary>
/// Formats a <see cref="GdprExportData"/> payload into a byte array for download.
/// Implementations produce JSON, CSV/ZIP, or other formats.
/// </summary>
public interface IGdprExportFormatter
{
    string ContentType { get; }
    string FileExtension { get; }
    ValueTask<byte[]> FormatAsync(GdprExportData data, CancellationToken ct);
}

/// <summary>
/// Aggregated personal-data payload used as input for GDPR export formatters.
/// </summary>
public sealed class GdprExportData
{
    public required string SubjectId { get; init; }
    public required string SubjectType { get; init; }
    public required DateTimeOffset ExportedAt { get; init; }
    public IReadOnlyDictionary<string, string>? PersonalData { get; init; }
    public IReadOnlyList<GdprConversationRecord>? Conversations { get; init; }
    public IReadOnlyList<GdprRecordingRecord>? Recordings { get; init; }
    public IReadOnlyList<GdprCallDetailRecord>? CallDetails { get; init; }
    public IReadOnlyList<GdprSurveyResponseRecord>? SurveyResponses { get; init; }
    public IReadOnlyList<GdprAuditRecord>? AuditTrail { get; init; }
}

public sealed record GdprConversationRecord(
    string ConversationId,
    string Channel,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public sealed record GdprRecordingRecord(
    string RecordingId,
    string ConversationId,
    int DurationSeconds,
    DateTimeOffset CreatedAt);

public sealed record GdprCallDetailRecord(
    string SessionId,
    string CallerNumber,
    string CalledNumber,
    int DurationSeconds,
    string Disposition,
    DateTimeOffset StartedAt);

public sealed record GdprSurveyResponseRecord(
    string SurveyId,
    string SurveyName,
    IReadOnlyDictionary<string, string> Answers,
    DateTimeOffset SubmittedAt);

public sealed record GdprAuditRecord(
    string Action,
    string EntityType,
    string EntityId,
    string? PerformedBy,
    DateTimeOffset OccurredAt);
