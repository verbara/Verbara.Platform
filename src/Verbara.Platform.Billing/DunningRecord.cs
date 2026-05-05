using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Billing;

public sealed class DunningRecord
{
    public required string DunningId { get; init; }
    public required string TenantId { get; init; }
    public required string InvoiceId { get; init; }
    public TenantStatus CurrentStage { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public bool IsPaused { get; set; }
    public bool IsActive { get; set; } = true;
}
