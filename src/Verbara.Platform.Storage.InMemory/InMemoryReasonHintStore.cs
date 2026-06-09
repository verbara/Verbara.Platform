using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryReasonHintStore : IReasonHintStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), ReasonHint> _items = new();

    public Task<ReasonHint?> GetByIdAsync(TenantId tenantId, EntityId hintId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, hintId), out var item);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<ReasonHint>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<ReasonHint> result = _items.Values
            .Where(h => h.TenantId == tenantId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ReasonHint>> ListByScopeAsync(TenantId tenantId, ReasonHintScope scope, string scopeRef, CancellationToken ct)
    {
        IReadOnlyList<ReasonHint> result = _items.Values
            .Where(h => h.TenantId == tenantId
                        && h.Scope == scope
                        && h.ScopeRef == scopeRef)
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(ReasonHint hint, CancellationToken ct)
    {
        // Upsert: an INSERT uses the incoming CreatedAt; an UPDATE preserves the
        // existing CreatedAt (the deterministic resolution tiebreak anchor).
        _items.AddOrUpdate(
            (hint.TenantId, hint.HintId),
            hint,
            (_, existing) => hint with { CreatedAt = existing.CreatedAt });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId hintId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, hintId), out _);
        return Task.CompletedTask;
    }
}
