using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public interface IContactStore
{
    Task<Contact?> GetByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);
    Task<Contact?> FindByAddressAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct);
    Task<PagedResult<Contact>> SearchAsync(TenantId tenantId, string? searchTerm, PagedQuery query, CancellationToken ct);
    Task SaveAsync(Contact contact, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);
}
