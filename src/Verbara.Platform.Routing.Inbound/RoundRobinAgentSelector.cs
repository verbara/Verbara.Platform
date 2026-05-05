using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Routing.Inbound;

public sealed class RoundRobinAgentSelector : IAgentSelector
{
    private readonly IAgentPresenceService _presenceService;
    private int _counter;

    public RoundRobinAgentSelector(IAgentPresenceService presenceService)
    {
        _presenceService = presenceService;
    }

    public async Task<EntityId?> SelectAgentAsync(
        TenantId tenantId,
        EntityId queueId,
        ChannelType channel,
        EntityId? preferredAgentId,
        CancellationToken ct)
    {
        var agents = await _presenceService.GetAvailableAgentsAsync(tenantId, queueId, channel, ct).ConfigureAwait(false);

        if (agents.Count == 0)
            return null;

        if (preferredAgentId.HasValue)
        {
            var preferred = agents.FirstOrDefault(a => a.AgentId == preferredAgentId.Value);
            if (preferred is not null)
                return preferred.AgentId;
        }

        var index = Math.Abs(Interlocked.Increment(ref _counter) - 1) % agents.Count;
        return agents[index].AgentId;
    }
}
