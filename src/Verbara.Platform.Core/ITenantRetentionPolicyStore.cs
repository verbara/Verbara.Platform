namespace Verbara.Platform.Core;

/// <summary>
/// Persistence contract for tenant data retention policies.
/// </summary>
public interface ITenantRetentionPolicyStore
{
    Task<TenantRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct);
    Task SaveAsync(TenantRetentionPolicy policy, CancellationToken ct);

    /// <summary>Returns tenants with at least one non-null retention field.</summary>
    Task<IReadOnlyList<TenantRetentionPolicy>> ListActiveAsync(CancellationToken ct);
}
