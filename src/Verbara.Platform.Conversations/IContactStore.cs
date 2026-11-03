using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public interface IContactStore
{
    Task<Contact?> GetByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);
    Task<Contact?> FindByAddressAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct);
    Task<PagedResult<Contact>> SearchAsync(TenantId tenantId, string? searchTerm, PagedQuery query, CancellationToken ct);
    Task SaveAsync(Contact contact, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);

    /// <summary>
    /// Adds to an existing contact the <paramref name="addresses"/> it lacks and, when it has neither a first nor a
    /// last name, sets its first name to <paramref name="firstNameIfUnnamed"/>, stamping
    /// <paramref name="updatedAt"/> and <paramref name="updatedBy"/>. Every other field is left as stored, so an edit
    /// made after the caller read the contact survives. One targeted update that never inserts: when the contact no
    /// longer exists (erased since it was read) nothing is written and <see langword="null"/> is returned; otherwise
    /// the contact as stored after the write.
    /// </summary>
    Task<Contact?> AddIdentityAsync(
        TenantId tenantId,
        EntityId contactId,
        IReadOnlyList<ChannelAddress> addresses,
        string? firstNameIfUnnamed,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct);
}
