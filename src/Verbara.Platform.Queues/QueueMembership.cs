using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public enum MembershipSource
{
    Skill = 0,
    Manual = 1,
}

public sealed class QueueMembership : ITenantScoped
{
    public required TenantId TenantId { get; init; }
    public required EntityId QueueId { get; init; }
    public required EntityId AgentId { get; init; }
    public int Penalty { get; set; }
    public required MembershipSource Source { get; init; }
    public bool IsExcluded { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// ADR-0026 channel-aware membership. <c>null</c> means the agent is a
    /// member for all channels the queue accepts (preserves pre-v2.6.0
    /// implicit behavior). A populated list restricts this membership to
    /// the listed channels only — both for routing eligibility (Phase B)
    /// and Asterisk queue_members sync (Phase A: sync only if NULL or
    /// list contains "voice"). An empty list is invalid; use IsExcluded
    /// instead for audit clarity.
    /// </summary>
    public IReadOnlyList<string>? AllowedChannels { get; set; }
}
