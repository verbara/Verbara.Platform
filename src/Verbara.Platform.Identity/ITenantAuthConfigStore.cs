namespace Verbara.Platform.Identity;

public interface ITenantAuthConfigStore
{
    Task<TenantAuthConfig?> GetAsync(string tenantId, CancellationToken ct);
    Task SaveAsync(TenantAuthConfig config, CancellationToken ct);
}
