using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public interface IUserStore
{
    Task<User?> GetByIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
    Task<User?> GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct);
    Task<PagedResult<User>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);
    Task SaveAsync(User user, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
}
