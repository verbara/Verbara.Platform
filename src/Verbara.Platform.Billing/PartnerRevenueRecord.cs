using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

public sealed class PartnerRevenueRecord
{
    public required EntityId RevenueId { get; init; }
    public required TenantId PartnerTenantId { get; init; }
    public required TenantId CustomerTenantId { get; init; }
    public required EntityId InvoiceId { get; init; }
    public required decimal GrossAmount { get; init; }
    public required decimal PlatformCost { get; init; }
    public required decimal PartnerMargin { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
