using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

/// <summary>
/// Checks tenant resource consumption against configured quota limits.
/// </summary>
public interface IQuotaEnforcementService
{
    /// <summary>Checks whether the tenant can consume additional units of the specified type.</summary>
    Task<QuotaCheckResult> CheckQuotaAsync(TenantId tenantId, UsageType type, decimal additionalQuantity, CancellationToken ct);

    /// <summary>Returns an overview of the tenant's quota usage across all metered types.</summary>
    Task<TenantQuotaStatus> GetQuotaStatusAsync(TenantId tenantId, CancellationToken ct);
}

/// <summary>
/// Overall quota status for a tenant, with per-type breakdown.
/// </summary>
public sealed record TenantQuotaStatus(
    TenantId TenantId,
    TenantQuota? Quota,
    IReadOnlyList<UsageSummary> CurrentUsage);
