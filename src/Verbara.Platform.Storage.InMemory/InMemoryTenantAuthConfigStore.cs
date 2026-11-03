using System.Collections.Concurrent;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryTenantAuthConfigStore : ITenantAuthConfigStore
{
    private readonly ConcurrentDictionary<string, TenantAuthConfig> _items = new();

    // conversations-close-after-inactivity (design D11): the per-tenant inactivity window override, kept apart from
    // the saved entities as the Postgres column is kept out of the upsert: GetAsync reads it, SaveAsync never writes it.
    private readonly ConcurrentDictionary<string, int?> _inactivityOverrides = new();

    public Task<TenantAuthConfig?> GetAsync(string tenantId, CancellationToken ct)
    {
        if (_items.TryGetValue(tenantId, out var config))
            config.InactivityCloseAfterHours = StoredOverride(tenantId);
        return Task.FromResult(config);
    }

    public Task SaveAsync(TenantAuthConfig config, CancellationToken ct)
    {
        config.InactivityCloseAfterHours = StoredOverride(config.TenantId);
        _items[config.TenantId] = config;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the tenant's inactivity window override the way an operator's SQL <c>UPDATE</c> does (test seam; design
    /// D11). The tenant's row is created first when it has none.
    /// </summary>
    internal void SeedInactivityCloseAfterHours(string tenantId, int? hours)
    {
        _inactivityOverrides[tenantId] = hours;
        _items.GetOrAdd(tenantId, id => new TenantAuthConfig { TenantId = id }).InactivityCloseAfterHours = hours;
    }

    private int? StoredOverride(string tenantId) =>
        _inactivityOverrides.TryGetValue(tenantId, out var hours) ? hours : null;
}
