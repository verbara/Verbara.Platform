namespace Asterisk.Platform.Audit;

/// <summary>
/// Parameters for searching audit entries.
/// </summary>
/// <remarks>
/// R5.2 PB.1 added the <see cref="ActionPrefix"/> and <see cref="ActorSearch"/> fields
/// so the audit log viewer can filter by hierarchical action namespaces
/// (<c>mfa.admin.</c> matches <c>mfa.admin.reset</c> and <c>mfa.admin.sessions_revoked</c>)
/// and by partial actor identifiers without forcing exact-match equality.
/// </remarks>
public sealed record AuditQuery(
    string? Action = null,
    string? EntityType = null,
    string? PerformedBy = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50,
    string? Category = null,
    string? Severity = null,
    string? ActorId = null,
    string? TargetId = null,
    string? TargetType = null,
    Guid? CorrelationId = null,
    string? ActionPrefix = null,
    string? ActorSearch = null);
