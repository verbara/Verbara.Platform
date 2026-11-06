using Verbara.Platform.Audit;
using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

/// <summary>
/// Writes a contact change and its audit record together: both persist, or neither does. A failure of either write, or
/// a cancellation between them, leaves the contact as it was and no record, so a retry applies the change once and
/// writes one record.
/// </summary>
/// <remarks>
/// Implementations: <c>PostgresContactAuditWriter</c> (one transaction on one connection of the data source the contact
/// store and the audit store share) and <c>InMemoryContactAuditWriter</c> (one lock; the previous contact is put back
/// when the record fails). If the audit store ever moves to a data source the contact store does not share, this
/// contract cannot hold and must not fall back to two separate writes.
/// </remarks>
public interface IContactAuditWriter
{
    /// <summary>Saves <paramref name="contact"/> (insert or replace) and inserts <paramref name="auditEntry"/>, as one unit.</summary>
    Task SaveAsync(Contact contact, AuditEntry auditEntry, CancellationToken ct);

    /// <summary>
    /// Deletes the contact and inserts <paramref name="auditEntry"/>, as one unit. When the tenant has no contact with
    /// that id nothing is deleted and nothing is recorded.
    /// </summary>
    /// <returns><see langword="true"/> when a contact was deleted and its record written.</returns>
    Task<bool> DeleteAsync(TenantId tenantId, EntityId contactId, AuditEntry auditEntry, CancellationToken ct);
}
