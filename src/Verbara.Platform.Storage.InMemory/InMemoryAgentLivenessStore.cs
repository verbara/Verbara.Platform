using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// In-memory <see cref="IAgentLivenessStore"/> (single-instance / test default).
/// Presence is the existence of an unexpired entry keyed by
/// <c>{tenantId}:{agentId}</c>; the stored <see cref="DateTimeOffset"/> is the
/// expiry instant. Expired entries are treated as absent (and lazily removed).
/// A <see cref="TimeProvider"/> is injected so tests can advance time
/// deterministically.
/// </summary>
public sealed class InMemoryAgentLivenessStore : IAgentLivenessStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _items = new();
    private readonly TimeProvider _clock;

    public InMemoryAgentLivenessStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    private static string Key(TenantId tenantId, EntityId agentId) => $"{tenantId.Value}:{agentId.Value}";

    public Task TouchAsync(TenantId tenantId, EntityId agentId, TimeSpan ttl, string nodeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _items[Key(tenantId, agentId)] = _clock.GetUtcNow() + ttl;
        return Task.CompletedTask;
    }

    public Task<bool> IsAliveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = Key(tenantId, agentId);
        if (_items.TryGetValue(key, out var expiry))
        {
            if (expiry > _clock.GetUtcNow())
                return Task.FromResult(true);

            // Lazily evict the expired entry so it is treated as absent.
            _items.TryRemove(key, out _);
        }
        return Task.FromResult(false);
    }

    public Task RemoveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _items.TryRemove(Key(tenantId, agentId), out _);
        return Task.CompletedTask;
    }
}
