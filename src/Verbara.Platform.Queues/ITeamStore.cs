using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public interface ITeamStore
{
    Task<Team?> GetByIdAsync(TenantId tenantId, EntityId teamId, CancellationToken ct);
    Task<PagedResult<Team>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);
    Task SaveAsync(Team team, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId teamId, CancellationToken ct);
}
