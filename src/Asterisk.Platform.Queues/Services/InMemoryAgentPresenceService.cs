using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Queues.Services;

public sealed class InMemoryAgentPresenceService : IAgentPresenceService
{
    private readonly IAgentStore _agentStore;
    private readonly IQueueStore _queueStore;
    private readonly IAgentCapacityService _capacityService;
    private readonly ConcurrentDictionary<(TenantId, EntityId), AgentState> _states = new();

    public InMemoryAgentPresenceService(
        IAgentStore agentStore,
        IQueueStore queueStore,
        IAgentCapacityService capacityService)
    {
        _agentStore = agentStore;
        _queueStore = queueStore;
        _capacityService = capacityService;
    }

    public async Task UpdateStateAsync(TenantId tenantId, EntityId agentId, AgentState newState, CancellationToken ct)
    {
        var agent = await _agentStore.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found for tenant '{tenantId}'.");

        var currentState = _states.GetValueOrDefault((tenantId, agentId), agent.State);
        AgentStateMachine.EnsureTransition(currentState, newState);

        _states[(tenantId, agentId)] = newState;
        agent.State = newState;
        await _agentStore.SaveAsync(agent, ct).ConfigureAwait(false);
    }

    public async Task<AgentState> GetStateAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        if (_states.TryGetValue((tenantId, agentId), out var state))
            return state;

        var agent = await _agentStore.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found for tenant '{tenantId}'.");

        return agent.State;
    }

    public async Task<IReadOnlyList<Agent>> GetAvailableAgentsAsync(
        TenantId tenantId,
        EntityId queueId,
        ChannelType channel,
        CancellationToken ct)
    {
        var queue = await _queueStore.GetByIdAsync(tenantId, queueId, ct).ConfigureAwait(false);

        var allAgents = await _agentStore.ListAsync(tenantId, new AgentQuery { PageSize = int.MaxValue }, ct).ConfigureAwait(false);

        var result = new List<Agent>();

        foreach (var agent in allAgents.Items)
        {
            // Filter: skills intersection (only if queue defines required skills)
            if (queue?.RequiredSkills.Count > 0 && !queue.RequiredSkills.Any(s => agent.Skills.Contains(s)))
                continue;

            // Filter: routable state
            var currentState = _states.GetValueOrDefault((tenantId, agent.AgentId), agent.State);
            if (!AgentStateMachine.IsRoutable(currentState))
                continue;

            // Filter: capacity available
            var hasCapacity = await _capacityService.HasCapacityAsync(tenantId, agent.AgentId, channel, ct).ConfigureAwait(false);
            if (!hasCapacity)
                continue;

            result.Add(agent);
        }

        return result;
    }
}
