using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryAgentStore : IAgentStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Agent> _items = new();

    public Task<Agent?> GetByIdAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, agentId), out var item);
        return Task.FromResult(item);
    }

    public Task<Agent?> GetByUserIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(a =>
            a.TenantId == tenantId &&
            a.UserId == userId);

        return Task.FromResult(result);
    }

    public Task<PagedResult<Agent>> ListAsync(TenantId tenantId, AgentQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(a => a.TenantId == tenantId)
            .Where(a => query.State == null || a.State == query.State)
            .Where(a => query.TeamId == null || a.TeamId == query.TeamId)
            .ToList();

        var totalCount = filtered.Count;
        var offset = (query.Page - 1) * query.PageSize;
        var items = filtered.Skip(offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<Agent>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(Agent agent, CancellationToken ct)
    {
        _items[(agent.TenantId, agent.AgentId)] = agent;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, agentId), out _);
        return Task.CompletedTask;
    }
}
