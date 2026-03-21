using Asterisk.Platform.Core;

namespace Asterisk.Platform.Queues.Services;

public interface IAgentCapacityService
{
    Task<bool> HasCapacityAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct);
    Task ReserveAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct);
    Task ReleaseAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct);
    Task<int> GetCurrentLoadAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct);
}
