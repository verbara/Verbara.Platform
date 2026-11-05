using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// Widened-window control for the contact resolver's write-back: wraps a real <see cref="IContactStore"/> and, right
/// after <see cref="FindByAddressAsync"/> returns a contact, runs <c>afterLookup</c> against the inner store — the
/// interleaving a GDPR purge or an agent's edit produces between the resolver's lookup and its write. Every other
/// member passes straight through.
/// </summary>
/// <remarks>Linked into Verbara.Platform.Storage.Postgres.Tests, so the control is the same on both stores.</remarks>
internal sealed class InterleaveAfterLookupContactStore(IContactStore inner, Func<IContactStore, Contact, Task> afterLookup)
    : IContactStore
{
    private bool _fired;

    public Task<Contact?> GetByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) =>
        inner.GetByIdAsync(tenantId, contactId, ct);

    public Task<IReadOnlyList<Contact>> FindAllByVoiceNumberAsync(
        TenantId tenantId, string canonicalNumber, int limit, CancellationToken ct) =>
        inner.FindAllByVoiceNumberAsync(tenantId, canonicalNumber, limit, ct);

    public async Task<Contact?> FindByAddressAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct)
    {
        var found = await inner.FindByAddressAsync(tenantId, address, ct);
        if (found is not null && !_fired)
        {
            _fired = true;
            await afterLookup(inner, found);
        }

        return found;
    }

    public Task<PagedResult<Contact>> SearchAsync(TenantId tenantId, string? searchTerm, PagedQuery query, CancellationToken ct) =>
        inner.SearchAsync(tenantId, searchTerm, query, ct);

    public Task SaveAsync(Contact contact, CancellationToken ct) => inner.SaveAsync(contact, ct);

    public Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) =>
        inner.DeleteAsync(tenantId, contactId, ct);

    public Task<Contact?> AddIdentityAsync(
        TenantId tenantId,
        EntityId contactId,
        IReadOnlyList<ChannelAddress> addresses,
        string? firstNameIfUnnamed,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct) =>
        inner.AddIdentityAsync(tenantId, contactId, addresses, firstNameIfUnnamed, updatedAt, updatedBy, ct);

    public Task<Contact> FindOrInsertByAddressAsync(TenantId tenantId, ChannelAddress key, Contact newContact, CancellationToken ct) =>
        inner.FindOrInsertByAddressAsync(tenantId, key, newContact, ct);

    public Task<Contact?> AttachWebChatSessionAsync(
        TenantId tenantId,
        EntityId contactId,
        ChannelAddress sessionAddress,
        string? firstNameIfUnnamed,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct) =>
        inner.AttachWebChatSessionAsync(tenantId, contactId, sessionAddress, firstNameIfUnnamed, updatedAt, updatedBy, ct);

    public Task<Contact?> SetCustomFieldIfAbsentAsync(
        TenantId tenantId,
        EntityId contactId,
        string key,
        string value,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct) =>
        inner.SetCustomFieldIfAbsentAsync(tenantId, contactId, key, value, updatedAt, updatedBy, ct);

    /// <summary>A detached copy of <paramref name="source"/>, the object an agent's edit would write.</summary>
    public static Contact Copy(Contact source)
    {
        var copy = new Contact
        {
            ContactId = source.ContactId,
            TenantId = source.TenantId,
            FirstName = source.FirstName,
            LastName = source.LastName,
            Company = source.Company,
            Segment = source.Segment,
            PreferredChannel = source.PreferredChannel,
            PreferredLanguage = source.PreferredLanguage,
            Timezone = source.Timezone,
            DoNotContact = source.DoNotContact,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            CreatedBy = source.CreatedBy,
            UpdatedBy = source.UpdatedBy,
        };
        foreach (var address in source.Addresses)
            copy.AddAddress(address);
        foreach (var kv in source.CustomFields)
            copy.SetCustomField(kv.Key, kv.Value);
        foreach (var kv in source.ChannelConsent)
            copy.SetConsent(kv.Key, kv.Value);
        return copy;
    }
}
