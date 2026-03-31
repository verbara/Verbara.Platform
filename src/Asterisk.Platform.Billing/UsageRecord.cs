using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// An individual consumption event recorded for billing purposes.
/// </summary>
public sealed class UsageRecord : ITenantScoped
{
    public required EntityId RecordId { get; init; }
    public required TenantId TenantId { get; init; }
    public required UsageType UsageType { get; init; }
    public required decimal Quantity { get; init; }
    public required UsageUnit Unit { get; init; }
    public string? Channel { get; init; }
    public string? ReferenceId { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
