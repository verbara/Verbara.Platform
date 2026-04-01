namespace Asterisk.Platform.Core;

/// <summary>
/// GDPR Article 17 — Right to Erasure. Purges all PII for a contact with tombstone.
/// </summary>
public interface IGdprPurgeService
{
    Task<PurgeResult> PurgeContactDataAsync(
        string tenantId, string contactId, string performedBy,
        string reason, CancellationToken ct);
}

/// <summary>
/// Result of a GDPR purge operation. EntitiesDeleted maps entity type to count.
/// </summary>
public sealed record PurgeResult(
    string PurgeId,
    Dictionary<string, int> EntitiesDeleted,
    DateTimeOffset PurgedAt);
