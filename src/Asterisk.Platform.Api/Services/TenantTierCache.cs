using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Services;

internal sealed class TenantTierCache
{
    private readonly ConcurrentDictionary<string, RateLimitTier> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RateLimitTier GetTier(string tenantId)
        => _cache.GetValueOrDefault(tenantId, RateLimitTier.Standard);

    public void SetTier(string tenantId, RateLimitTier tier)
        => _cache[tenantId] = tier;

    public void Remove(string tenantId)
        => _cache.TryRemove(tenantId, out _);
}
