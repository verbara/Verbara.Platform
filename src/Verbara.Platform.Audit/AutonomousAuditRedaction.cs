namespace Verbara.Platform.Audit;

/// <summary>
/// Shared constants and helpers for the GDPR Art. 17 redaction of autonomous-disposition audit
/// records (ADR-0034 Decision 4). On a valid contact erasure the contact-identifying linkage is
/// nulled while the decision-fact is retained under the Art. 17(3) legal-defence exemption.
/// </summary>
public static class AutonomousAuditRedaction
{
    /// <summary>
    /// Action name of the autonomous-disposition commit audit record. Only records with this action
    /// (the only audit rows that carry an AI decision fact tied to a contact's conversation) are
    /// redacted; all other audit rows are out of scope for this redaction path.
    /// </summary>
    public const string AutonomousCommitAction = "typification.autonomous.commit";

    /// <summary>Metadata key holding the contact-linked conversation ID (redacted to a tombstone).</summary>
    public const string ConversationMetadataKey = "conversation";

    /// <summary>Metadata marker stamped on a redacted record so the redaction is itself auditable.</summary>
    public const string RedactedMarkerKey = "redacted";

    /// <summary>Tombstone value substituted for the redacted contact linkage.</summary>
    public const string RedactedValue = "[redacted]";

    /// <summary>
    /// Produces the redacted copy of an autonomous-commit audit entry: the contact-identifying
    /// linkage (<see cref="AuditEntry.TargetId"/> and the <c>conversation</c> metadata key) is
    /// nulled/tombstoned while the decision-fact (node path, leaf node, confidence, timestamp,
    /// AI actor, retention floor) is retained. The integrity hash is recomputed over the redacted
    /// canonical fields so the record stays tamper-evident in its redacted state. Returns the
    /// original instance unchanged when the entry is not an autonomous-commit record or has already
    /// been redacted (idempotent).
    /// </summary>
    public static AuditEntry Redact(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.Equals(entry.Action, AutonomousCommitAction, StringComparison.Ordinal))
            return entry;
        if (entry.Metadata is not null && entry.Metadata.ContainsKey(RedactedMarkerKey))
            return entry; // already redacted — idempotent

        IReadOnlyDictionary<string, string>? redactedMetadata = null;
        if (entry.Metadata is { Count: > 0 })
        {
            var copy = new Dictionary<string, string>(entry.Metadata, StringComparer.Ordinal);
            if (copy.ContainsKey(ConversationMetadataKey))
                copy[ConversationMetadataKey] = RedactedValue;
            copy[RedactedMarkerKey] = "true";
            redactedMetadata = copy;
        }
        else
        {
            redactedMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RedactedMarkerKey] = "true",
            };
        }

        return new AuditEntry
        {
            EntryId = entry.EntryId,
            TenantId = entry.TenantId,
            Action = entry.Action,
            Category = entry.Category,
            Severity = entry.Severity,
            ActorId = entry.ActorId,
            ActorType = entry.ActorType,
            // Contact linkage redacted: the conversation ID identified the contact.
            TargetId = null,
            TargetType = entry.TargetType,
            CorrelationId = entry.CorrelationId,
            Changes = entry.Changes,
            Metadata = redactedMetadata,
            OccurredAt = entry.OccurredAt,
            ImpersonatorId = entry.ImpersonatorId,
            RetainUntil = entry.RetainUntil,
            IntegrityHash = DefaultAuditService.ComputeIntegrityHash(
                entry.TenantId, entry.ActorType, entry.ActorId, entry.Action,
                entry.TargetType, targetId: null, entry.OccurredAt, redactedMetadata),
        };
    }
}
