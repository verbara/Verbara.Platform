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

    /// <summary>
    /// Returns the contact holding <paramref name="key"/>, or inserts <paramref name="newContact"/> when none does. The
    /// find and the insert are one step per tenant and key, so concurrent callers with one new key resolve one contact.
    /// The caller tells the two outcomes apart by comparing the returned id with <paramref name="newContact"/>'s.
    /// </summary>
    Task<Contact> FindOrInsertByAddressAsync(TenantId tenantId, ChannelAddress key, Contact newContact, CancellationToken ct);

    /// <summary>
    /// Adds a WebChat session address to an existing contact, drops the oldest session addresses beyond the newest 50
    /// in the same write, and fills the first name when the contact has neither a first nor a last name. Every other
    /// address and field is left as stored. Never inserts: <see langword="null"/> when the contact no longer exists.
    /// </summary>
    Task<Contact?> AttachWebChatSessionAsync(
        TenantId tenantId,
        EntityId contactId,
        ChannelAddress sessionAddress,
        string? firstNameIfUnnamed,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct);

    /// <summary>
    /// Sets the custom field <paramref name="key"/> only while the contact does not hold it, touching nothing else of
    /// the row. Never inserts: <see langword="null"/> when no row matched (no contact, or the key already set);
    /// otherwise the contact as stored after the write.
    /// </summary>
    Task<Contact?> SetCustomFieldIfAbsentAsync(
        TenantId tenantId,
        EntityId contactId,
        string key,
        string value,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct);
}
