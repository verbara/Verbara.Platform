using Asterisk.Platform.Core;

namespace Asterisk.Platform.Bot;

/// <summary>
/// Per-tenant bot configuration, associating a bot identity with a default flow and handoff settings.
/// </summary>
public sealed class BotConfiguration : ITenantScoped
{
    public required EntityId BotId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; set; }
    public required EntityId DefaultFlowId { get; init; }
    public EntityId? FallbackQueueId { get; set; }
    public double ConfidenceThreshold { get; set; } = 0.7;
    public int MaxTurnsBeforeHandoff { get; set; } = 20;
    public bool IsActive { get; set; } = true;
}
