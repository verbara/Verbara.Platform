using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Aggregated usage for a tenant within a specific time period and usage type.
/// </summary>
public sealed class UsageSummary : ITenantScoped
{
    public required TenantId TenantId { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required UsageType UsageType { get; init; }
    public required decimal TotalQuantity { get; set; }
    public required int RecordCount { get; set; }
    public required DateTimeOffset LastUpdatedAt { get; set; }
}
