using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Identity.Redis.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Verbara.Platform.Identity.Redis.Tests;

/// <summary>
/// Tests the opt-in DI extension: it must REPLACE previously registered in-memory
/// <see cref="IMfaPendingCache"/> / <see cref="IPasswordResetCache"/> /
/// <see cref="IJtiRevocationCache"/> singletons with the Redis-backed implementations
/// and share the same <see cref="IConnectionMultiplexer"/>.
/// </summary>
[Collection("Redis")]
public sealed class IdentityRedisServiceCollectionExtensionsTests
{
    private readonly RedisFixture _fixture;

    public IdentityRedisServiceCollectionExtensionsTests(RedisFixture fixture) => _fixture = fixture;

    [Fact]
    public void AddVerbaraPlatformIdentityRedis_ShouldReplaceInMemoryRegistrations_WhenCalled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(_fixture.Redis);

        // Start with in-memory defaults (as Program.cs does)
        services.AddSingleton<IMfaPendingCache, InMemoryMfaPendingCache>();
        services.AddSingleton<IPasswordResetCache, InMemoryPasswordResetCache>();
        services.AddSingleton<IJtiRevocationCache, InMemoryJtiRevocationCache>();

        services.AddVerbaraPlatformIdentityRedis(o =>
        {
            o.ConnectionString = _fixture.ConnectionString;
            o.KeyPrefix = "di-test:";
        });

        using var sp = services.BuildServiceProvider();
        var mfa = sp.GetRequiredService<IMfaPendingCache>();
        var reset = sp.GetRequiredService<IPasswordResetCache>();
        var jti = sp.GetRequiredService<IJtiRevocationCache>();

        mfa.Should().BeOfType<RedisMfaPendingCache>();
        reset.Should().BeOfType<RedisPasswordResetCache>();
        jti.Should().BeOfType<RedisJtiRevocationCache>();
    }

    [Fact]
    public async Task AddVerbaraPlatformIdentityRedis_ShouldReuseExistingMultiplexer_WhenOneIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(_fixture.Redis);

        services.AddVerbaraPlatformIdentityRedis(o =>
        {
            o.ConnectionString = "unused:6379"; // Should be ignored because IConnectionMultiplexer already registered
            o.KeyPrefix = "di-reuse:";
        });

        using var sp = services.BuildServiceProvider();

        var multiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
        multiplexer.Should().BeSameAs(_fixture.Redis);

        // Resolved cache can still talk to Redis end-to-end
        var cache = sp.GetRequiredService<IMfaPendingCache>();
        await cache.StoreAsync("reuse-token", new MfaPendingEntry
        {
            UserId = "u",
            TenantId = "t",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        }, CancellationToken.None);

        var result = await cache.TakeAsync("reuse-token", CancellationToken.None);
        result.Should().NotBeNull();
    }
}
