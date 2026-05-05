using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

public interface IRateCardStore
{
    Task SaveAsync(RateCard rateCard, CancellationToken ct);
    Task<RateCard?> GetByIdAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct);
    Task<RateCard?> GetActiveAsync(TenantId tenantId, DateTimeOffset asOf, CancellationToken ct);
    Task<IReadOnlyList<RateCard>> ListAsync(TenantId tenantId, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct);
}
