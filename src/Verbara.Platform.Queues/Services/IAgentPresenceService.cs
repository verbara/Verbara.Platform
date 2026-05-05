using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

public interface IAgentPresenceService
{
    Task UpdateStateAsync(TenantId tenantId, EntityId agentId, AgentState newState, CancellationToken ct);
    Task<AgentState> GetStateAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<IReadOnlyList<Agent>> GetAvailableAgentsAsync(TenantId tenantId, EntityId queueId, ChannelType channel, CancellationToken ct);
}
