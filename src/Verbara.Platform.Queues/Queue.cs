using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

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

    // Per-queue auto-answer default (3B.2b). An agent whose own AutoAnswer is
    // null inherits this for calls delivered from this queue; an explicit agent
    // override wins. Defaults false (manual answer) — the industry-standard
    // opt-in posture (Amazon Connect / Genesys).
    public bool AutoAnswerDefault { get; set; }

    // Per-queue CSAT solicitation config (csat-runner Phase A). Null = never
    // configured; a configured value round-trips via the queue-update endpoint
    // and the four additive queue_configs columns. A queue with a null Csat (or
    // Csat.Enabled == false) is never solicited for CSAT.
    public CsatConfig? Csat { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }
}
