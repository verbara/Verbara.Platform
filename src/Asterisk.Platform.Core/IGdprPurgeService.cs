namespace Asterisk.Platform.Core;

/// <summary>
/// GDPR Article 17 — Right to Erasure. Purges all PII for a contact or user with tombstone.
/// </summary>
public interface IGdprPurgeService
{
    Task<PurgeResult> PurgeContactDataAsync(
        string tenantId, string contactId, string performedBy,
        string reason, CancellationToken ct);

    Task<PurgeResult> PurgeUserDataAsync(
        string tenantId, string userId, string performedBy,
        string reason, CancellationToken ct);

    Task<UserPurgePreview> PreviewUserPurgeAsync(
        string tenantId, string userId, CancellationToken ct);
}

/// <summary>
/// Result of a GDPR purge operation. EntitiesDeleted maps entity type to count.
/// </summary>
public sealed record PurgeResult(
    string PurgeId,
    Dictionary<string, int> EntitiesDeleted,
    DateTimeOffset PurgedAt);

/// <summary>
/// Preview of entities that would be affected by a user purge operation.
/// </summary>
public sealed record UserPurgePreview(
    string UserId,
    string TenantId,
    int AuthEventCount,
    int AuditTrailCount,
    DateTimeOffset PreviewedAt);
