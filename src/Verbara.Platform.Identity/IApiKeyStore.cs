using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

public interface IApiKeyStore
{
    Task<ApiKey?> GetByIdAsync(TenantId tenantId, EntityId keyId, CancellationToken ct);
    Task<ApiKey?> GetByHashAsync(string hashedKey, CancellationToken ct);
    Task<PagedResult<ApiKey>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);
    Task SaveAsync(ApiKey apiKey, CancellationToken ct);
    Task RevokeAsync(TenantId tenantId, EntityId keyId, CancellationToken ct);

    /// <summary>
    /// Stamps <see cref="ApiKey.LastUsedAt"/> for the key identified by
    /// <paramref name="keyId"/>. Called fire-and-forget by the auth
    /// middleware after a successful validation, debounced in-process to
    /// ≤ 1 write per minute per key (R5.2 PC.5 / B.12).
    /// </summary>
    Task UpdateLastUsedAsync(EntityId keyId, DateTimeOffset usedAt, CancellationToken ct);
}
