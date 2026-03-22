namespace Asterisk.Platform.Audit;

/// <summary>
/// Parameters for searching audit entries.
/// </summary>
public sealed record AuditQuery(
    string? Action = null,
    string? EntityType = null,
    string? PerformedBy = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);
