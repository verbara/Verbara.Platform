using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class PermissionResolver
{
    private readonly IUserRoleStore _userRoleStore;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public PermissionResolver(IUserRoleStore userRoleStore)
    {
        _userRoleStore = userRoleStore;
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
    }

    public void InvalidateTenant(TenantId tenantId)
    {
        var prefix = $"{tenantId.Value}:";
        foreach (var key in _cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                _cache.TryRemove(key, out _);
        }
    }

    private sealed record CacheEntry(IReadOnlySet<string> Permissions, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
