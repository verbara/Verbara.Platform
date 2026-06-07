using System.Collections.Concurrent;
using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

public sealed class InMemoryAgentCapacityService : IAgentCapacityService
{
    private readonly IAgentStore _agentStore;
    private readonly ConcurrentDictionary<(TenantId, EntityId, ChannelType), int> _load = new();

    public InMemoryAgentCapacityService(IAgentStore agentStore)
    {
        _agentStore = agentStore;
    }

    public async Task<bool> HasCapacityAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        var agent = await _agentStore.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false);
        if (agent is null)
            return false;

        // W6-A4 will resolve via IAgentCapacityResolver (tenant default) + add chat-pool + MaxTotal
        var max = agent.CapacityOverride.ToEffective(new ChannelCapacity()).GetMax(channel);
        if (max <= 0)
            return false;

        var current = _load.GetValueOrDefault((tenantId, agentId, channel), 0);
        return current < max;
    }

    public Task ReserveAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        _load.AddOrUpdate((tenantId, agentId, channel), 1, static (_, current) => current + 1);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        _load.AddOrUpdate(
            (tenantId, agentId, channel),
            0,
            static (_, current) => current > 0 ? current - 1 : 0);
        return Task.CompletedTask;
    }

    public Task<int> GetCurrentLoadAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        var current = _load.GetValueOrDefault((tenantId, agentId, channel), 0);
        return Task.FromResult(current);
    }
}
