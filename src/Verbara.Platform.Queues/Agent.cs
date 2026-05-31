using System.Text.Json.Serialization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

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

    // The plaintext SIP password is a secret (required plaintext by Asterisk
    // realtime ps_auths). It must NEVER serialize from the entity over HTTP —
    // the ONLY deliberate exposure is the self-scoped AgentMeResponseDto on
    // GET /agents/me, which copies it into its own field in C#. JsonIgnore here
    // is defense-in-depth so admin/list endpoints returning the raw entity
    // (or any future endpoint) can never leak it.
    [JsonIgnore]
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
