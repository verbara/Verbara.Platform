using Asterisk.Platform.Core;

namespace Asterisk.Platform.Queues;

public sealed class Agent : ITenantScoped, IAuditable
{
    public required EntityId AgentId { get; init; }
    public required TenantId TenantId { get; init; }
    public required EntityId UserId { get; init; }
    public required string DisplayName { get; set; }
    public required AgentState State { get; set; }
    public ChannelCapacity Capacity { get; set; } = new();
    public EntityId? TeamId { get; set; }
    public IReadOnlyList<string> Skills { get; set; } = [];
    public string? Extension { get; set; }
    public string? SipPassword { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }

    public bool CanAcceptWork => AgentStateMachine.IsRoutable(State);

    public bool HasCapacity(ChannelType channel)
    {
        if (!CanAcceptWork)
        {
            return false;
        }

        return Capacity.GetMax(channel) > 0;
    }

    public void TransitionTo(AgentState newState)
    {
        AgentStateMachine.EnsureTransition(State, newState);
        State = newState;
    }
}
