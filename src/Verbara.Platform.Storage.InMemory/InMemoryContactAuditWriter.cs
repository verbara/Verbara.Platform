using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// Dev/test mirror of <c>PostgresContactAuditWriter</c> (contacts-writes-follow-their-permissions, design D17). Under
/// one lock it keeps the contact as it was, writes the change, then the record. When the record fails, or the call is
/// cancelled between the two writes, it puts the kept contact back (or removes a contact that did not exist), so
/// neither side persists, and rethrows. A failed contact write leaves nothing to undo.
/// </summary>
internal sealed class InMemoryContactAuditWriter : IContactAuditWriter
{
    private readonly IContactStore _contacts;
    private readonly IAuditStore _audit;
    private readonly Lock _gate = new();

    public InMemoryContactAuditWriter(IContactStore contacts, IAuditStore audit)
    {
        _contacts = contacts;
        _audit = audit;
    }

    // The in-memory stores complete synchronously, so each call is drained inside the lock's scope; a Lock.Scope is a
    // ref struct and cannot span an await, so these methods stay synchronous (as InMemoryTypificationCorrectionAuditWriter).
    public Task SaveAsync(Contact contact, AuditEntry auditEntry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(auditEntry);

        try
        {
            using (_gate.EnterScope())
            {
                var kept = _contacts.GetByIdAsync(contact.TenantId, contact.ContactId, ct).GetAwaiter().GetResult();
                _contacts.SaveAsync(contact, ct).GetAwaiter().GetResult();
                RecordOrUndo(auditEntry, undo: () =>
                {
                    if (kept is null)
                        _contacts.DeleteAsync(contact.TenantId, contact.ContactId, CancellationToken.None).GetAwaiter().GetResult();
                    else
                        _contacts.SaveAsync(kept, CancellationToken.None).GetAwaiter().GetResult();
                }, ct);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public Task<bool> DeleteAsync(TenantId tenantId, EntityId contactId, AuditEntry auditEntry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auditEntry);

        try
        {
            using (_gate.EnterScope())
            {
                var kept = _contacts.GetByIdAsync(tenantId, contactId, ct).GetAwaiter().GetResult();
                if (kept is null)
                    return Task.FromResult(false);

                _contacts.DeleteAsync(tenantId, contactId, ct).GetAwaiter().GetResult();
                RecordOrUndo(auditEntry, undo: () => _contacts.SaveAsync(kept, CancellationToken.None).GetAwaiter().GetResult(), ct);
            }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            return Task.FromException<bool>(ex);
        }
    }

    private void RecordOrUndo(AuditEntry auditEntry, Action undo, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            _audit.SaveAsync(auditEntry, ct).GetAwaiter().GetResult();
        }
        catch
        {
            undo();
            throw;
        }
    }
}
