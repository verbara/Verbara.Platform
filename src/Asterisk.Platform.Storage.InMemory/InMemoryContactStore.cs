using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryContactStore : IContactStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Contact> _items = new();

    public Task<Contact?> GetByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, contactId), out var item);
        return Task.FromResult(item);
    }

    public Task<Contact?> FindByAddressAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(c =>
            c.TenantId == tenantId &&
            c.Addresses.Any(a => a.Channel == address.Channel && a.Address == address.Address));

        return Task.FromResult(result);
    }

    public Task<PagedResult<Contact>> SearchAsync(TenantId tenantId, string? searchTerm, PagedQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(c => c.TenantId == tenantId)
            .Where(c => searchTerm == null ||
                (c.FirstName != null && c.FirstName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) ||
                (c.LastName != null && c.LastName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) ||
                (c.Company != null && c.Company.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<Contact>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(Contact contact, CancellationToken ct)
    {
        _items[(contact.TenantId, contact.ContactId)] = contact;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, contactId), out _);
        return Task.CompletedTask;
    }
}
