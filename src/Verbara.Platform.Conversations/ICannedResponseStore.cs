using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public interface ICannedResponseStore
{
    Task<CannedResponse?> GetByIdAsync(TenantId tenantId, EntityId responseId, CancellationToken ct);
    Task<IReadOnlyList<CannedResponse>> ListByTenantAsync(TenantId tenantId, CancellationToken ct);
    Task<IReadOnlyList<CannedResponse>> SearchAsync(TenantId tenantId, string query, CancellationToken ct);
    Task SaveAsync(CannedResponse response, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId responseId, CancellationToken ct);
}
