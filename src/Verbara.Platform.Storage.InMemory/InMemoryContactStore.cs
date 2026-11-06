using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryContactStore : IContactStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Contact> _items = new();

    // Serialises every write, so a write's lookup and its change are one step: a delete or a replacing save lands wholly
    // before or wholly after it, never in between. Reads stay lock-free.
    private readonly Lock _writeGate = new();

    // A copy, as Postgres reads a new row: a caller that changes the contact it read changes nothing stored until it
    // saves, so a write that fails leaves the stored contact as it was (contacts-writes-follow-their-permissions D17).
    // Taken under the write gate, so it never sees a write half applied.
    public Task<Contact?> GetByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        lock (_writeGate)
            return Task.FromResult(_items.TryGetValue((tenantId, contactId), out var item) ? Copy(item) : null);
    }

    public Task<Contact?> FindByAddressAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(c =>
            c.TenantId == tenantId &&
            c.Addresses.Any(a => a.Channel == address.Channel && a.Address == address.Address));

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Contact>> FindAllByChannelAddressAsync(
        TenantId tenantId, ChannelType channel, string address, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        var key = ChannelAddressMatch.KeyOf(channel, address);
        if (key.Length == 0)
            return Task.FromResult<IReadOnlyList<Contact>>([]);

        IReadOnlyList<Contact> result = _items.Values
            .Where(c => c.TenantId == tenantId && c.Addresses.Any(a => a.Channel == channel && Matches(a.Address)))
            .OrderByDescending(c => c.DoNotContact)
            .ThenByDescending(c => c.ChannelConsent.TryGetValue(channel, out var consented) && !consented)
            .ThenBy(c => c.ContactId.Value, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .ToList();
        return Task.FromResult(result);

        bool Matches(string stored) => string.Equals(ChannelAddressMatch.KeyOf(channel, stored), key, StringComparison.Ordinal);
    }

    public Task<PagedResult<Contact>> SearchAsync(TenantId tenantId, string? searchTerm, PagedQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(c => c.TenantId == tenantId)
            .Where(c => searchTerm == null ||
                (c.FirstName != null && c.FirstName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) ||
                (c.LastName != null && c.LastName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) ||
                (c.Company != null && c.Company.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) ||
                (c.CustomFields.TryGetValue(Contact.WebChatEmailField, out var email) && email.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<Contact>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(Contact contact, CancellationToken ct)
    {
        lock (_writeGate)
            _items[(contact.TenantId, contact.ContactId)] = contact;
        return Task.CompletedTask;
    }

    public Task<Contact?> AddIdentityAsync(
        TenantId tenantId,
        EntityId contactId,
        IReadOnlyList<ChannelAddress> addresses,
        string? firstNameIfUnnamed,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        // Never inserts: a contact deleted since the caller read it stays deleted.
        lock (_writeGate)
        {
            if (!_items.TryGetValue((tenantId, contactId), out var contact))
                return Task.FromResult<Contact?>(null);

            foreach (var address in addresses)
                contact.AddAddress(address);
            if (contact.FirstName is null && contact.LastName is null && firstNameIfUnnamed is not null)
                contact.FirstName = firstNameIfUnnamed;
            contact.UpdatedAt = updatedAt;
            contact.UpdatedBy = updatedBy;
            return Task.FromResult<Contact?>(contact);
        }
    }

    public Task<Contact> FindOrInsertByAddressAsync(TenantId tenantId, ChannelAddress key, Contact newContact, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(newContact);
        lock (_writeGate)
        {
            var held = _items.Values.FirstOrDefault(c => c.TenantId == tenantId && c.Addresses.Contains(key));
            if (held is not null)
                return Task.FromResult(held);

            _items[(tenantId, newContact.ContactId)] = newContact;
            return Task.FromResult(newContact);
        }
    }

    public Task<Contact?> AttachWebChatSessionAsync(
        TenantId tenantId,
        EntityId contactId,
        ChannelAddress sessionAddress,
        string? firstNameIfUnnamed,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionAddress);
        lock (_writeGate)
        {
            if (!_items.TryGetValue((tenantId, contactId), out var stored))
                return Task.FromResult<Contact?>(null);

            // A new row, as on Postgres: whoever read the contact holds a snapshot, enumerated outside this lock.
            var contact = Copy(stored);
            contact.AddAddress(sessionAddress);
            var sessions = contact.Addresses.Where(a => a.IsWebChatSessionAddress()).ToList();
            foreach (var oldest in sessions.Take(sessions.Count - Contact.MaxWebChatSessionAddresses))
                contact.RemoveAddress(oldest);
            if (contact.FirstName is null && contact.LastName is null)
                contact.FirstName = firstNameIfUnnamed;
            contact.UpdatedAt = updatedAt;
            contact.UpdatedBy = updatedBy;
            _items[(tenantId, contactId)] = contact;
            return Task.FromResult<Contact?>(contact);
        }
    }

    public Task<Contact?> SetCustomFieldIfAbsentAsync(
        TenantId tenantId,
        EntityId contactId,
        string key,
        string value,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct)
    {
        lock (_writeGate)
        {
            if (!_items.TryGetValue((tenantId, contactId), out var stored) || stored.CustomFields.ContainsKey(key))
                return Task.FromResult<Contact?>(null);

            var contact = Copy(stored);
            contact.SetCustomField(key, value);
            contact.UpdatedAt = updatedAt;
            contact.UpdatedBy = updatedBy;
            _items[(tenantId, contactId)] = contact;
            return Task.FromResult<Contact?>(contact);
        }
    }

    public Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        lock (_writeGate)
            _items.TryRemove((tenantId, contactId), out _);
        return Task.CompletedTask;
    }

    private static Contact Copy(Contact source)
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
        foreach (var field in source.CustomFields)
            copy.SetCustomField(field.Key, field.Value);
        foreach (var consent in source.ChannelConsent)
            copy.SetConsent(consent.Key, consent.Value);
        return copy;
    }
}
