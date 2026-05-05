using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

public interface IServiceAccountStore
{
    Task<ServiceAccount?> GetByIdAsync(TenantId tenantId, EntityId accountId, CancellationToken ct);
    Task<PagedResult<ServiceAccount>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);
    Task SaveAsync(ServiceAccount account, CancellationToken ct);
    Task DeactivateAsync(TenantId tenantId, EntityId accountId, CancellationToken ct);
}
