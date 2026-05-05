using Verbara.Platform.Api.Services;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Redis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// AHH Phase 1 — DI wire-up for the Auth Hotpath Hardening cache decorators.
/// Replaces the unkeyed <see cref="IUserStore"/> / <see cref="ITenantAuthConfigStore"/>
/// registrations (left in place by <c>AddPostgresStorage</c> /
/// <c>AddInMemoryStorage</c>) with cache decorators that resolve their inner
/// store via the <see cref="AuthHotpathCacheKeys"/> keyed registrations.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent. Registers <see cref="IMemoryCache"/> via
/// <c>MemoryCacheServiceCollectionExtensions.AddMemoryCache</c> if not
/// already present.
/// </para>
/// <para>
/// Cross-replica invalidation is engaged automatically when
/// <see cref="RedisAuthCacheInvalidator"/> is registered (i.e. when
/// <c>AddVerbaraPlatformIdentityRedis</c> + <c>AddAuthHotpathRedisInvalidation</c>
/// have been called). When Redis is not configured, staleness is bounded by
/// the local TTL.
/// </para>
/// </remarks>
public static class AuthHotpathCachingExtensions
{
    /// <summary>
    /// Wires the AHH Phase 1 cache decorators on top of the previously
    /// registered <see cref="IUserStore"/> + <see cref="ITenantAuthConfigStore"/>
    /// implementations. Call AFTER <c>AddPostgresStorage</c> /
    /// <c>AddInMemoryStorage</c>.
    /// </summary>
    public static IServiceCollection AddAuthHotpathCaching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();

        // Replace unkeyed IUserStore (currently a delegating alias to the
        // keyed concrete) with the cache decorator. The decorator itself
        // resolves the keyed inner via GetRequiredKeyedService.
        RemoveUnkeyed<IUserStore>(services);
        services.AddSingleton<CachedUserStore>(sp => new CachedUserStore(
            sp.GetRequiredKeyedService<IUserStore>(AuthHotpathCacheKeys.UserStoreInner),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetService<IAuthCachePublisher>()));
        services.AddSingleton<IUserStore>(sp => sp.GetRequiredService<CachedUserStore>());

        // Same for ITenantAuthConfigStore.
        RemoveUnkeyed<ITenantAuthConfigStore>(services);
        services.AddSingleton<CachedTenantAuthConfigStore>(sp => new CachedTenantAuthConfigStore(
            sp.GetRequiredKeyedService<ITenantAuthConfigStore>(AuthHotpathCacheKeys.TenantAuthConfigStoreInner),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetService<IAuthCachePublisher>()));
        services.AddSingleton<ITenantAuthConfigStore>(sp => sp.GetRequiredService<CachedTenantAuthConfigStore>());

        return services;
    }

    /// <summary>
    /// Wires the cross-replica Redis pubsub invalidator. Call only when
    /// <c>AddVerbaraPlatformIdentityRedis</c> has been called and the
    /// process runs in a multi-replica deployment. Idempotent.
    /// </summary>
    public static IServiceCollection AddAuthHotpathRedisInvalidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Each cache decorator (and PermissionResolver) registers itself as a
        // sink. The invalidator subscribes to Redis pubsub and dispatches
        // invalidation messages to all sinks. Only one logical instance
        // exists in the container; idempotent.
        services.TryAddSingleton<RedisAuthCacheInvalidator>();

        // v1.14.2 — IAuthCachePublisher resolves to the lightweight
        // RedisAuthCachePublisher (NO sink dependency), NOT the concrete
        // RedisAuthCacheInvalidator. The decorators are themselves sinks
        // (registered below via TryAddEnumerable), and the invalidator
        // constructor needs IEnumerable<sinks>. Pre-v1.14.2, decorators
        // took `RedisAuthCacheInvalidator?` directly → singleton
        // resolution looped through itself (decorator → invalidator →
        // sinks → decorator) and deadlocked at startup. By making the
        // publisher a SEPARATE singleton sharing only IConnectionMultiplexer,
        // the cycle is structurally broken.
        services.TryAddSingleton<RedisAuthCachePublisher>();
        services.TryAddSingleton<IAuthCachePublisher>(sp => sp.GetRequiredService<RedisAuthCachePublisher>());

        // Sinks are resolved by the invalidator constructor (IEnumerable<...>).
        // The TImplementation generic must differ across calls — otherwise
        // TryAddEnumerable treats the three registrations as duplicates of
        // the same impl-type and throws "Implementation type cannot be ...
        // indistinguishable from other services registered for ...".
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILocalAuthCacheInvalidationSink, CachedUserStore>(
            sp => sp.GetRequiredService<CachedUserStore>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILocalAuthCacheInvalidationSink, CachedTenantAuthConfigStore>(
            sp => sp.GetRequiredService<CachedTenantAuthConfigStore>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILocalAuthCacheInvalidationSink, PermissionResolver>(
            sp => sp.GetRequiredService<PermissionResolver>()));

        services.AddHostedService(sp => sp.GetRequiredService<RedisAuthCacheInvalidator>());

        return services;
    }

    private static void RemoveUnkeyed<TService>(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var d = services[i];
            // Keyed registrations have a non-null ServiceKey; only remove the
            // unkeyed alias (left in place by AddPostgresStorage to point at
            // the keyed concrete).
            if (d.ServiceType == typeof(TService) && !d.IsKeyedService)
                services.RemoveAt(i);
        }
    }
}
