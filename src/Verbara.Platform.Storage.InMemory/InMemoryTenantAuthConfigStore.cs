using System.Collections.Concurrent;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryTenantAuthConfigStore : ITenantAuthConfigStore
{
    private readonly ConcurrentDictionary<string, TenantAuthConfig> _items = new();

    public Task<TenantAuthConfig?> GetAsync(string tenantId, CancellationToken ct)
    {
        _items.TryGetValue(tenantId, out var config);
        return Task.FromResult(config);
    }

    public Task SaveAsync(TenantAuthConfig config, CancellationToken ct)
    {
        _items[config.TenantId] = config;
        return Task.CompletedTask;
    }
}
