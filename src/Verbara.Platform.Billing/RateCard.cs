using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

public sealed class RateCard : ITenantScoped
{
    public required EntityId RateCardId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public required IReadOnlyList<RateEntry> Rates { get; init; }
    public bool IsDefault { get; init; }
}

public sealed class RateEntry
{
    public required UsageType UsageType { get; init; }
    public required decimal UnitPrice { get; init; }
    public decimal IncludedQuantity { get; init; }
    public IReadOnlyList<RateTier>? Tiers { get; init; }
}

public sealed class RateTier
{
    public required decimal FromQuantity { get; init; }
    public decimal? ToQuantity { get; init; }
    public required decimal UnitPrice { get; init; }
}
