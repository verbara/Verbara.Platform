using Asterisk.Platform.Identity;
using Asterisk.Platform.Identity.Redis;
using Microsoft.Extensions.Caching.Memory;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// AHH Phase 1 — IMemoryCache decorator over <see cref="ITenantAuthConfigStore"/>.
/// Caches <see cref="TenantAuthConfig"/> reads for a configurable TTL (default
/// 60 s) keyed by <c>tenant-auth:{tenantId}</c>; <see cref="SaveAsync"/> writes
/// pass through to the inner store and invalidate the cache locally.
/// </summary>
/// <remarks>
/// Removes 5–10 ms × 2–3 DB round-trips per <c>POST /auth/login</c>
/// (TenantAuthConfigMfaPolicyEvaluator + lockout config + password policy).
/// Cross-replica invalidation is delivered through
/// <c>Asterisk.Platform.Identity.Redis.RedisAuthCacheInvalidator</c> when
/// Redis is registered; without Redis, staleness is bounded by the local TTL.
/// See ADR-0010 + the AHH Phase 1 plan section.
/// </remarks>
internal sealed class CachedTenantAuthConfigStore : ITenantAuthConfigStore, ILocalAuthCacheInvalidationSink
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly ITenantAuthConfigStore _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;
    private readonly IAuthCachePublisher? _invalidator;

    public CachedTenantAuthConfigStore(
        ITenantAuthConfigStore inner,
        IMemoryCache cache,
        IAuthCachePublisher? invalidator = null,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);

        _inner = inner;
        _cache = cache;
        _invalidator = invalidator;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>Cache key namespace for tenant-auth-config entries.</summary>
    public static string CacheKey(string tenantId) => $"tenant-auth:{tenantId}";

    public async Task<TenantAuthConfig?> GetAsync(string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var key = CacheKey(tenantId);
        if (_cache.TryGetValue<TenantAuthConfig?>(key, out var cached))
            return cached;

        var fresh = await _inner.GetAsync(tenantId, ct).ConfigureAwait(false);
        // Cache both populated AND null results — null caching prevents
        // a thundering herd of DB reads for unconfigured tenants.
        _cache.Set(key, fresh, _ttl);
        return fresh;
    }

    public async Task SaveAsync(TenantAuthConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _inner.SaveAsync(config, ct).ConfigureAwait(false);

        // Invalidate locally + cluster-wide AFTER the write returns so the
        // next read goes to DB and observes what was persisted.
        _cache.Remove(CacheKey(config.TenantId));
        if (_invalidator is not null)
            await _invalidator.PublishTenantAuthAsync(config.TenantId, ct).ConfigureAwait(false);
    }

    // ─── ILocalAuthCacheInvalidationSink ────────────────────────────────────

    public void InvalidateTenantAuth(string tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        _cache.Remove(CacheKey(tenantId));
    }

    public void InvalidateUser(string tenantId, string userId, string? email)
    {
        // Not our concern — handled by CachedUserStore.
    }

    public void InvalidatePermissions(string tenantId, string userId)
    {
        // Not our concern — handled by PermissionResolver sink.
    }
}
