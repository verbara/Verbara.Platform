using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Stores;

public interface IDispositionStore
{
    Task<Disposition?> GetByIdAsync(TenantId tenantId, EntityId dispositionId, CancellationToken ct);
    Task<IReadOnlyList<Disposition>> ListAsync(TenantId tenantId, CancellationToken ct);
    Task SaveAsync(Disposition disposition, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId dispositionId, CancellationToken ct);
}
