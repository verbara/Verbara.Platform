using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public interface IApiKeyStore
{
    Task<ApiKey?> GetByIdAsync(TenantId tenantId, EntityId keyId, CancellationToken ct);
    Task<ApiKey?> GetByHashAsync(string hashedKey, CancellationToken ct);
    Task<PagedResult<ApiKey>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);
    Task SaveAsync(ApiKey apiKey, CancellationToken ct);
    Task RevokeAsync(TenantId tenantId, EntityId keyId, CancellationToken ct);
}
