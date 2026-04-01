namespace Asterisk.Platform.Core;

/// <summary>
/// Tombstone record of a GDPR data purge — contains NO PII, only metadata.
/// </summary>
public sealed record PurgeEntry
{
    public required string PurgeId { get; init; }
    public required string TenantId { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public required string PerformedBy { get; init; }
    public required string Reason { get; init; }
    public required Dictionary<string, int> EntitiesDeleted { get; init; }
    public required DateTimeOffset PurgedAt { get; init; }
}
