using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Identity.Auth.Jwt;
using Verbara.Platform.Identity.Mfa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Verbara.Platform.Identity.Redis.DependencyInjection;

/// <summary>
/// DI registration extensions for the Redis-backed Identity caches.
/// </summary>
public static class IdentityRedisServiceCollectionExtensions
{
    /// <summary>
    /// Registers Redis-backed implementations of <see cref="IMfaPendingCache"/>,
    /// <see cref="IPasswordResetCache"/>, and <see cref="IJtiRevocationCache"/>,
    /// replacing any previously registered implementations (e.g. the in-memory defaults
    /// from the Platform.Api bootstrap).
    ///
    /// Also registers <see cref="IConnectionMultiplexer"/> as a singleton if one is not
    /// already present in the container, so Redis can be shared with other packages
    /// (e.g. <c>Verbara.Sdk.Pro.Cluster.Redis</c>).
    /// </summary>
    public static IServiceCollection AddVerbaraPlatformIdentityRedis(
        this IServiceCollection services,
        Action<RedisIdentityOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.TryAddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisIdentityOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new InvalidOperationException(
                    "RedisIdentityOptions.ConnectionString is required when no " +
                    "IConnectionMultiplexer has been registered elsewhere in the container.");
            }
            return ConnectionMultiplexer.Connect(options.ConnectionString);
        });

        RemoveExisting<IMfaPendingCache>(services);
        services.AddSingleton<IMfaPendingCache>(sp => new RedisMfaPendingCache(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<IOptions<RedisIdentityOptions>>()));

        RemoveExisting<IPasswordResetCache>(services);
        services.AddSingleton<IPasswordResetCache>(sp => new RedisPasswordResetCache(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<IOptions<RedisIdentityOptions>>()));

        // R5.2 PA.3 / B.9 — replace InMemoryJtiRevocationCache so revoked-jti state
        // survives failover across multiple Platform API instances.
        RemoveExisting<IJtiRevocationCache>(services);
        services.AddSingleton<IJtiRevocationCache>(sp => new RedisJtiRevocationCache(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<IOptions<RedisIdentityOptions>>()));

        // R5.4 S5.9 — replace InMemoryJwtKeyStore so the JWT signing-key rotation
        // pool survives failover and so multiple Platform API nodes share the
        // same active key + grace-window keys (zero-downtime rotation).
        RemoveExisting<IJwtKeyStore>(services);
        services.AddSingleton<IJwtKeyStore>(sp => new RedisJwtKeyStore(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<IOptions<RedisIdentityOptions>>()));

        return services;
    }

    private static void RemoveExisting<TService>(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService))
                services.RemoveAt(i);
        }
    }
}
