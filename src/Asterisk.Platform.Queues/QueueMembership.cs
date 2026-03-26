using Asterisk.Platform.Core;

namespace Asterisk.Platform.Queues;

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
}
