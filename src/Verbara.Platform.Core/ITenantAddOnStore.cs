namespace Verbara.Platform.Core;

public interface ITenantAddOnStore
{
    Task<IReadOnlyList<TenantAddOn>> GetAsync(string tenantId, CancellationToken ct = default);
    Task UpsertAsync(TenantAddOn addOn, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, PlanFeature feature, CancellationToken ct = default);
}
