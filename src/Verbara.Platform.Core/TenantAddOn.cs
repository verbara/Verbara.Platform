namespace Verbara.Platform.Core;

public sealed class TenantAddOn
{
    public required string TenantId { get; init; }
    public required PlanFeature Feature { get; init; }
    public required DateTimeOffset EnabledAt { get; init; }
}
