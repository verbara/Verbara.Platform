using System.Diagnostics.CodeAnalysis;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Queues;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Queue is the domain entity name")]
public sealed class Queue : ITenantScoped, IAuditable
{
    public required EntityId QueueId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public int? MaxWaiting { get; set; }
    public SlaPolicyTarget? SlaTargets { get; set; }
    public QueueOverflowRule? OverflowRule { get; set; }
    public HoursOfOperation? Hours { get; set; }
    public WrapUpConfig WrapUp { get; set; } = new();
    public IReadOnlyList<string> RequiredSkills { get; set; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }
}
