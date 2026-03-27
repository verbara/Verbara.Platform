using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public interface IAuthEventStore
{
    Task SaveAsync(AuthEvent authEvent, CancellationToken ct);
    Task<PagedResult<AuthEvent>> ListByTenantAsync(string tenantId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<AuthEvent>> ListByUserAsync(string tenantId, string userId, int page, int pageSize, CancellationToken ct);
}
