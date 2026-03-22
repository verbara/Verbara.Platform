using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), ApiKey> _items = new();

    public Task<ApiKey?> GetByIdAsync(TenantId tenantId, EntityId keyId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, keyId), out var item);
        return Task.FromResult(item);
    }

    public Task<ApiKey?> GetByHashAsync(string hashedKey, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(k => k.HashedKey == hashedKey);
        return Task.FromResult(result);
    }

    public Task<PagedResult<ApiKey>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(k => k.TenantId == tenantId)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<ApiKey>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(ApiKey apiKey, CancellationToken ct)
    {
        _items[(apiKey.TenantId, apiKey.KeyId)] = apiKey;
        return Task.CompletedTask;
    }

    public Task RevokeAsync(TenantId tenantId, EntityId keyId, CancellationToken ct)
    {
        if (_items.TryGetValue((tenantId, keyId), out var key))
            key.IsRevoked = true;

        return Task.CompletedTask;
    }
}
