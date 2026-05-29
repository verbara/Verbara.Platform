using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests;

internal sealed class InMemoryQueueMembershipStore : IQueueMembershipStore
{
    private readonly ConcurrentDictionary<(string TenantId, string QueueId, string AgentId), QueueMembership> _store = new();

    public Task<IReadOnlyList<QueueMembership>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<QueueMembership> result = _store.Values
            .Where(m => m.TenantId.Value == tenantId.Value)
            .OrderBy(m => m.QueueId.Value)
            .ThenBy(m => m.AgentId.Value)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<QueueMembership>> ListByQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        IReadOnlyList<QueueMembership> result = _store.Values
            .Where(m => m.TenantId.Value == tenantId.Value && m.QueueId.Value == queueId.Value)
            .OrderBy(m => m.Penalty)
            .ThenBy(m => m.AgentId.Value)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<QueueMembership>> ListByAgentAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        IReadOnlyList<QueueMembership> result = _store.Values
            .Where(m => m.TenantId.Value == tenantId.Value && m.AgentId.Value == agentId.Value)
            .OrderBy(m => m.QueueId.Value)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<QueueMembership?> GetAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct)
    {
        _store.TryGetValue((tenantId.Value, queueId.Value, agentId.Value), out var membership);
        return Task.FromResult(membership);
    }

    public Task SaveAsync(QueueMembership membership, CancellationToken ct)
    {
        _store[(membership.TenantId.Value, membership.QueueId.Value, membership.AgentId.Value)] = membership;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct)
    {
        _store.TryRemove((tenantId.Value, queueId.Value, agentId.Value), out _);
        return Task.CompletedTask;
    }

    public Task DeleteAllForQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        var keys = _store.Keys
            .Where(k => k.TenantId == tenantId.Value && k.QueueId == queueId.Value)
            .ToList();
        foreach (var key in keys)
            _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task DeleteAllForAgentAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        var keys = _store.Keys
            .Where(k => k.TenantId == tenantId.Value && k.AgentId == agentId.Value)
            .ToList();
        foreach (var key in keys)
            _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
