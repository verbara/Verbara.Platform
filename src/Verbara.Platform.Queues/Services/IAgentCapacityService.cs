using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

public interface IAgentCapacityService
{
    Task<bool> HasCapacityAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct);
    Task ReserveAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct);

    /// <summary>
    /// Reserves one unit of the agent's capacity on <paramref name="channel"/> only if it is free, deciding the check
    /// and the reservation as one step within this process; returns whether it reserved. Unlike
    /// <see cref="HasCapacityAsync"/> followed by <see cref="ReserveAsync"/>, two concurrent callers cannot both take
    /// the last unit (agent-started-conversations, design D14). When it throws, nothing stays reserved.
    /// </summary>
    Task<bool> TryReserveAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct);
    Task ReleaseAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct);
    Task<int> GetCurrentLoadAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct);
}
