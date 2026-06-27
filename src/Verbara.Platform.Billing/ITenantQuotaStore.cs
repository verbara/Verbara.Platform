using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

/// <summary>
/// Persistence contract for tenant quota configurations.
/// </summary>
public interface ITenantQuotaStore
{
    /// <summary>Gets the quota for a tenant, or null if not configured.</summary>
    Task<TenantQuota?> GetAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>Creates or updates the quota for a tenant.</summary>
    Task UpsertAsync(TenantQuota quota, CancellationToken ct);

    /// <summary>Deletes the quota for a tenant.</summary>
    Task DeleteAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>
    /// Returns all tenant quotas whose <see cref="TenantQuota.AiCreditsMonthly"/> is non-null — the
    /// work-list for the <c>CreditGrantMintWorker</c> subscription-grant mint. Tenants with a null AI-credit
    /// allowance (unlimited / pay-as-you-go) are excluded.
    /// </summary>
    Task<IReadOnlyList<TenantQuota>> ListWithAiCreditsAsync(CancellationToken ct);
}
