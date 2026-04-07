using System.Collections.Concurrent;
using Asterisk.Platform.Core.Branding;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantBrandingStore : ITenantBrandingStore
{
    private readonly ConcurrentDictionary<string, TenantBranding> _store = new();

    public ValueTask<TenantBranding?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        _store.TryGetValue(tenantId, out var branding);
        return ValueTask.FromResult(branding);
    }

    public ValueTask<TenantBranding?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default)
    {
        var result = _store.Values
            .FirstOrDefault(b => string.Equals(b.Subdomain, subdomain, StringComparison.OrdinalIgnoreCase));
        return ValueTask.FromResult(result);
    }

    public ValueTask UpsertAsync(TenantBranding branding, CancellationToken ct = default)
    {
        branding.UpdatedAt = DateTimeOffset.UtcNow;
        _store[branding.TenantId] = branding;
        return ValueTask.CompletedTask;
    }
}
