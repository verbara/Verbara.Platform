using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemorySchemaBindingStore : ISchemaBindingStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), SchemaBinding> _items = new();

    public Task<SchemaBinding?> GetByIdAsync(TenantId tenantId, EntityId bindingId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, bindingId), out var item);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<SchemaBinding>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<SchemaBinding> result = _items.Values
            .Where(b => b.TenantId == tenantId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SchemaBinding>> ListByScopeAsync(TenantId tenantId, BindingScope scope, string? scopeRef, CancellationToken ct)
    {
        IReadOnlyList<SchemaBinding> result = _items.Values
            .Where(b => b.TenantId == tenantId
                        && b.Scope == scope
                        && (scopeRef is null ? b.ScopeRef is null : b.ScopeRef == scopeRef))
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(SchemaBinding binding, CancellationToken ct)
    {
        // Upsert: an INSERT uses the incoming CreatedAt; an UPDATE preserves the
        // existing CreatedAt (the deterministic resolution tiebreak anchor).
        _items.AddOrUpdate(
            (binding.TenantId, binding.BindingId),
            binding,
            (_, existing) => binding with { CreatedAt = existing.CreatedAt });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId bindingId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, bindingId), out _);
        return Task.CompletedTask;
    }
}
