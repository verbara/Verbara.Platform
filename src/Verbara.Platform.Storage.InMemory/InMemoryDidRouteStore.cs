using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryDidRouteStore : IDidRouteStore
{
    private readonly ConcurrentDictionary<(string TenantId, string RouteId), DidRoute> _store = new();

    // Serializes the check-then-write in Create/Update so the UNIQUE(tenant_id, did)
    // invariant Postgres enforces atomically also holds here (no read-then-write race).
    private readonly object _writeLock = new();

    public Task<IReadOnlyList<DidRoute>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<DidRoute> result = _store.Values
            .Where(r => r.TenantId.Value == tenantId.Value)
            .OrderBy(r => r.Did, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<DidRoute>> ListActiveAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<DidRoute> result = _store.Values
            .Where(r => r.TenantId.Value == tenantId.Value && r.IsActive)
            .OrderBy(r => r.Did, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<DidRoute?> GetAsync(TenantId tenantId, EntityId routeId, CancellationToken ct)
    {
        _store.TryGetValue((tenantId.Value, routeId.Value), out var route);
        return Task.FromResult(route);
    }

    public Task<DidRoute?> GetByDidAsync(TenantId tenantId, string did, CancellationToken ct)
    {
        var route = _store.Values.FirstOrDefault(
            r => r.TenantId.Value == tenantId.Value && string.Equals(r.Did, did, StringComparison.Ordinal));
        return Task.FromResult(route);
    }

    public Task<DidRoute> CreateAsync(DidRoute route, CancellationToken ct)
    {
        var routeId = route.RouteId;
        if (string.IsNullOrWhiteSpace(routeId.Value))
            routeId = EntityId.New();

        var stored = new DidRoute
        {
            RouteId = routeId,
            TenantId = route.TenantId,
            Did = route.Did,
            QueueId = route.QueueId,
            IsActive = route.IsActive,
            CreatedAt = route.CreatedAt,
            UpdatedAt = route.UpdatedAt,
        };

        lock (_writeLock)
        {
            // Mirror the Postgres UNIQUE (tenant_id, did) constraint atomically so a
            // duplicate DID for the same tenant is a domain-meaningful conflict, not a
            // silent overwrite (and two concurrent creates can't both slip through).
            var duplicate = _store.Values.Any(
                r => r.TenantId.Value == route.TenantId.Value
                     && string.Equals(r.Did, route.Did, StringComparison.Ordinal));
            if (duplicate)
            {
                throw new InvalidOperationException(
                    $"A DID route already exists for did '{route.Did}' in tenant '{route.TenantId.Value}'.");
            }
            _store[(stored.TenantId.Value, stored.RouteId.Value)] = stored;
        }
        return Task.FromResult(stored);
    }

    public Task UpdateAsync(DidRoute route, CancellationToken ct)
    {
        lock (_writeLock)
        {
            var key = (route.TenantId.Value, route.RouteId.Value);
            // Mirror Postgres `UPDATE ... WHERE tenant_id AND route_id`: a missing or
            // cross-tenant row is a no-op (0 rows affected), never a phantom insert.
            if (!_store.ContainsKey(key))
                return Task.CompletedTask;
            // Mirror UNIQUE (tenant_id, did): renaming onto another route's DID in the
            // same tenant is a conflict (Postgres raises 23505).
            var collision = _store.Values.Any(
                r => r.TenantId.Value == route.TenantId.Value
                     && r.RouteId.Value != route.RouteId.Value
                     && string.Equals(r.Did, route.Did, StringComparison.Ordinal));
            if (collision)
            {
                throw new InvalidOperationException(
                    $"A DID route already exists for did '{route.Did}' in tenant '{route.TenantId.Value}'.");
            }
            _store[key] = route;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId routeId, CancellationToken ct)
    {
        _store.TryRemove((tenantId.Value, routeId.Value), out _);
        return Task.CompletedTask;
    }
}
