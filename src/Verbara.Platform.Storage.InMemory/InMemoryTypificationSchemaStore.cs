using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryTypificationSchemaStore : ITypificationSchemaStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId, int), TypificationSchema> _items = new();

    public Task<TypificationSchema?> GetByIdAsync(TenantId tenantId, EntityId schemaId, int? version, CancellationToken ct)
    {
        if (version is { } v)
        {
            _items.TryGetValue((tenantId, schemaId, v), out var exact);
            return Task.FromResult(exact);
        }

        var latest = _items.Values
            .Where(s => s.TenantId == tenantId && s.SchemaId == schemaId)
            .OrderByDescending(s => s.Version)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<TypificationSchema?> GetLatestPublishedAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct)
    {
        var latest = _items.Values
            .Where(s => s.TenantId == tenantId && s.SchemaId == schemaId && s.IsPublished)
            .OrderByDescending(s => s.Version)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<IReadOnlyList<TypificationSchema>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<TypificationSchema> result = _items.Values
            .Where(s => s.TenantId == tenantId)
            .GroupBy(s => s.SchemaId)
            .Select(g => g.OrderByDescending(s => s.Version).First())
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(TypificationSchema schema, CancellationToken ct)
    {
        _items[(schema.TenantId, schema.SchemaId, schema.Version)] = schema;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct)
    {
        foreach (var key in _items.Keys.Where(k => k.Item1 == tenantId && k.Item2 == schemaId).ToList())
            _items.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
