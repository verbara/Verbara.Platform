using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Redis;

namespace Verbara.Platform.Api.Services;

internal sealed class PermissionResolver : ILocalAuthCacheInvalidationSink
{
    private readonly IUserRoleStore _userRoleStore;
    private readonly IAuthCachePublisher? _invalidator;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public PermissionResolver(IUserRoleStore userRoleStore, IAuthCachePublisher? invalidator = null)
    {
        _userRoleStore = userRoleStore;
        _invalidator = invalidator;
    }

    public async Task<IReadOnlySet<string>> ResolveAsync(
        TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var cacheKey = $"{tenantId.Value}:{userId.Value}";

        if (_cache.TryGetValue(cacheKey, out var entry) && !entry.IsExpired)
            return entry.Permissions;

        var permissions = await _userRoleStore.GetEffectivePermissionsAsync(tenantId, userId, ct);

        _cache[cacheKey] = new CacheEntry(permissions, DateTimeOffset.UtcNow.Add(CacheTtl));

        return permissions;
    }

    public static bool HasPermission(IReadOnlySet<string> effectivePermissions, string requiredPermission)
    {
        return effectivePermissions.Contains(requiredPermission);
    }

    public void InvalidateUser(TenantId tenantId, EntityId userId)
    {
        var cacheKey = $"{tenantId.Value}:{userId.Value}";
        _cache.TryRemove(cacheKey, out _);
        // Fire-and-forget: cluster-wide invalidation. We do not await here so
        // the caller (typically a role-mutation handler) is not blocked on
        // Redis pubsub. Failure is logged inside the invalidator.
        _ = _invalidator?.PublishPermissionsAsync(tenantId.Value, userId.Value);
    }

    public void InvalidateTenant(TenantId tenantId)
    {
        var prefix = $"{tenantId.Value}:";
        foreach (var key in _cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                _cache.TryRemove(key, out _);
        }
        // Tenant-wide invalidation isn't a separate wire-format type in
        // Phase 1; cross-replica callers re-resolve on their next per-user
        // miss, bounded by the 5-min TTL. ADR-0010 documents this as a
        // bounded-staleness window for tenant-level role rotations.
    }

    // ─── ILocalAuthCacheInvalidationSink ────────────────────────────────────

    public void InvalidatePermissions(string tenantId, string userId)
    {
        var cacheKey = $"{tenantId}:{userId}";
        _cache.TryRemove(cacheKey, out _);
    }

    public void InvalidateTenantAuth(string tenantId)
    {
        // Not our concern — handled by CachedTenantAuthConfigStore.
    }

    public void InvalidateUser(string tenantId, string userId, string? email)
    {
        // The string overload is the Redis-pubsub fan-in path; route it to
        // the typed local-cache key so role-cache stays consistent with
        // user-cache invalidations on remote replicas.
        var cacheKey = $"{tenantId}:{userId}";
        _cache.TryRemove(cacheKey, out _);
    }

    private sealed record CacheEntry(IReadOnlySet<string> Permissions, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
