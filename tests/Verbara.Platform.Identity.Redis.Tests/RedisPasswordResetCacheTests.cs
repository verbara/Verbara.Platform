using Verbara.Platform.Identity.Mfa;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Identity.Redis.Tests;

/// <summary>
/// Integration tests for <see cref="RedisPasswordResetCache"/>. Cover put+take roundtrip,
/// TTL expiry, single-consumption semantics, and key-prefix isolation.
/// </summary>
[Collection("Redis")]
public sealed class RedisPasswordResetCacheTests
{
    private readonly RedisFixture _fixture;

    public RedisPasswordResetCacheTests(RedisFixture fixture) => _fixture = fixture;

    private RedisPasswordResetCache CreateCache(string? keyPrefix = null)
    {
        var options = Options.Create(new RedisIdentityOptions
        {
            KeyPrefix = keyPrefix ?? $"test:{Guid.NewGuid():N}:identity:",
        });
        return new RedisPasswordResetCache(_fixture.Redis, options);
    }

    [Fact]
    public async Task StoreAsync_ShouldMakeEntryRetrievable_WhenCalled()
    {
        var cache = CreateCache();
        var entry = new PasswordResetEntry
        {
            UserId = "user-2",
            TenantId = "tenant-b",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };

        await cache.StoreAsync("reset-abc", entry, CancellationToken.None);
        var result = await cache.TakeAsync("reset-abc", CancellationToken.None);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(entry.UserId);
        result.TenantId.Should().Be(entry.TenantId);
        result.ExpiresAt.Should().BeCloseTo(entry.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TakeAsync_ShouldReturnNull_WhenKeyMissing()
    {
        var cache = CreateCache();

        var result = await cache.TakeAsync("does-not-exist", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TakeAsync_ShouldRemoveEntry_WhenCalled()
    {
        var cache = CreateCache();
        var entry = new PasswordResetEntry
        {
            UserId = "user-2",
            TenantId = "tenant-b",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };

        await cache.StoreAsync("reset-xyz", entry, CancellationToken.None);
        _ = await cache.TakeAsync("reset-xyz", CancellationToken.None);
        var secondTake = await cache.TakeAsync("reset-xyz", CancellationToken.None);

        secondTake.Should().BeNull(because: "TakeAsync must consume the entry on first call");
    }

    [Fact]
    public async Task StoreAsync_ShouldExpire_WhenTtlElapses()
    {
        var cache = CreateCache();
        var entry = new PasswordResetEntry
        {
            UserId = "user-2",
            TenantId = "tenant-b",
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(500),
        };

        await cache.StoreAsync("reset-ttl", entry, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(750)); // fence-allow: SETTLE — wait for real Redis TTL expiry (integration, CI-excluded)
        var result = await cache.TakeAsync("reset-ttl", CancellationToken.None);

        result.Should().BeNull(because: "Redis TTL must evict the entry once ExpiresAt has passed");
    }

    [Fact]
    public async Task TakeAsync_ShouldReturnNull_WhenEntryStoredExpired()
    {
        var cache = CreateCache();
        var entry = new PasswordResetEntry
        {
            UserId = "user-2",
            TenantId = "tenant-b",
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(-1),
        };

        await cache.StoreAsync("reset-stale", entry, CancellationToken.None);
        var result = await cache.TakeAsync("reset-stale", CancellationToken.None);

        result.Should().BeNull(because: "expired entries must never be returned");
    }

    [Fact]
    public async Task KeyPrefix_ShouldIsolateTenants_WhenSamePrefixWouldCollide()
    {
        var cacheA = CreateCache(keyPrefix: "reset-iso-a:");
        var cacheB = CreateCache(keyPrefix: "reset-iso-b:");

        var entryA = new PasswordResetEntry
        {
            UserId = "user-A",
            TenantId = "tenant-A",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };

        await cacheA.StoreAsync("shared-reset", entryA, CancellationToken.None);
        var readFromB = await cacheB.TakeAsync("shared-reset", CancellationToken.None);

        readFromB.Should().BeNull(because: "a different KeyPrefix must not see cache-A's entries");

        var readFromA = await cacheA.TakeAsync("shared-reset", CancellationToken.None);
        readFromA.Should().NotBeNull();
        readFromA!.UserId.Should().Be("user-A");
    }
}
