using Asterisk.Platform.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// IMemoryCache decorator over <see cref="ITenantIpAllowlistStore"/>.
/// Caches <c>ListAsync</c> results for 60s; mutations write through and
/// invalidate the cache key locally. Mirrors the
/// <see cref="CachedTenantAuthConfigStore"/> pattern (AHH Phase 1) but
/// without cross-replica invalidation — allowlist mutations are operator-
/// driven (rare) and 60s staleness is acceptable per spec §3.3.
/// </summary>
internal sealed class CachedTenantIpAllowlistStore : ITenantIpAllowlistStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly ITenantIpAllowlistStore _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CachedTenantIpAllowlistStore(
        ITenantIpAllowlistStore inner,
        IMemoryCache cache,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        _inner = inner;
        _cache = cache;
        _ttl = ttl ?? DefaultTtl;
    }

    public static string CacheKey(string tenantId) => $"ip-allowlist:{tenantId}";

    public async Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct)
    {
        var key = CacheKey(tenantId);
        if (_cache.TryGetValue<IReadOnlyList<IpAllowlistEntry>>(key, out var cached) && cached is not null)
            return cached;

        var fresh = await _inner.ListAsync(tenantId, ct);
        _cache.Set(key, fresh, _ttl);
        return fresh;
    }

    public async Task<IpAllowlistEntry> AddAsync(
        string tenantId,
        string cidr,
        string? description,
        string? createdByUserId,
        CancellationToken ct)
    {
        var entry = await _inner.AddAsync(tenantId, cidr, description, createdByUserId, ct);
        _cache.Remove(CacheKey(tenantId));
        return entry;
    }

    public async Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct)
    {
        var removed = await _inner.RemoveAsync(tenantId, entryId, ct);
        if (removed)
            _cache.Remove(CacheKey(tenantId));
        return removed;
    }

    public Task<int> CountAsync(string tenantId, CancellationToken ct) => _inner.CountAsync(tenantId, ct);
}
