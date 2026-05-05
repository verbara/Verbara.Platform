using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public interface ICaseStore
{
    Task<Case?> GetByIdAsync(TenantId tenantId, EntityId caseId, CancellationToken ct);
    Task<PagedResult<Case>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);
    Task SaveAsync(Case caseEntity, CancellationToken ct);
}
