using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryAgentCapacityStore : IAgentCapacityStore
{
    private readonly ConcurrentDictionary<(string TenantId, string AgentId), AgentCapacityRecord> _store = new();

    public Task<IReadOnlyList<AgentCapacityRecord>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        var result = _store.Values
            .Where(r => r.TenantId == tenantId.Value)
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentCapacityRecord>>(result);
    }

    public Task UpsertAsync(AgentCapacityRecord record, CancellationToken ct)
    {
        _store[(record.TenantId, record.AgentId)] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        _store.TryRemove((tenantId.Value, agentId.Value), out _);
        return Task.CompletedTask;
    }

    public Task ClearAllAsync(CancellationToken ct)
    {
        _store.Clear();
        return Task.CompletedTask;
    }
}
