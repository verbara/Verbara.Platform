using Verbara.Platform.Identity.Mfa;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Identity.Redis.Tests;

/// <summary>
/// Integration tests for <see cref="RedisMfaPendingCache"/>. Cover put+take roundtrip,
/// TTL expiry, single-consumption semantics, and key-prefix isolation. A per-test
/// random <see cref="RedisIdentityOptions.KeyPrefix"/> keeps runs independent without
/// needing to FLUSHDB.
/// </summary>
[Collection("Redis")]
public sealed class RedisMfaPendingCacheTests
{
    private readonly RedisFixture _fixture;

    public RedisMfaPendingCacheTests(RedisFixture fixture) => _fixture = fixture;

    private RedisMfaPendingCache CreateCache(string? keyPrefix = null)
    {
        var options = Options.Create(new RedisIdentityOptions
        {
            KeyPrefix = keyPrefix ?? $"test:{Guid.NewGuid():N}:identity:",
        });
        return new RedisMfaPendingCache(_fixture.Redis, options);
    }

    [Fact]
    public async Task StoreAsync_ShouldMakeEntryRetrievable_WhenCalled()
    {
        var cache = CreateCache();
        var entry = new MfaPendingEntry
        {
            UserId = "user-1",
            TenantId = "tenant-a",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        await cache.StoreAsync("token-abc", entry, CancellationToken.None);
        var result = await cache.TakeAsync("token-abc", CancellationToken.None);

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
        var entry = new MfaPendingEntry
        {
            UserId = "user-1",
            TenantId = "tenant-a",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        await cache.StoreAsync("token-xyz", entry, CancellationToken.None);
        _ = await cache.TakeAsync("token-xyz", CancellationToken.None);
        var secondTake = await cache.TakeAsync("token-xyz", CancellationToken.None);

        secondTake.Should().BeNull(because: "TakeAsync must consume the entry on first call");
    }

    [Fact]
    public async Task StoreAsync_ShouldExpire_WhenTtlElapses()
    {
        var cache = CreateCache();
        var entry = new MfaPendingEntry
        {
            UserId = "user-1",
            TenantId = "tenant-a",
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(500),
        };

        await cache.StoreAsync("token-ttl", entry, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        var result = await cache.TakeAsync("token-ttl", CancellationToken.None);

        result.Should().BeNull(because: "Redis TTL must evict the entry once ExpiresAt has passed");
    }

    [Fact]
    public async Task TakeAsync_ShouldReturnNull_WhenEntryStoredExpired()
    {
        var cache = CreateCache();
        var entry = new MfaPendingEntry
        {
            UserId = "user-1",
            TenantId = "tenant-a",
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(-1),
        };

        // StoreAsync should no-op for already-expired entries — nothing to evict.
        await cache.StoreAsync("token-stale", entry, CancellationToken.None);
        var result = await cache.TakeAsync("token-stale", CancellationToken.None);

        result.Should().BeNull(because: "expired entries must never be returned");
    }

    [Fact]
    public async Task KeyPrefix_ShouldIsolateTenants_WhenSamePrefixWouldCollide()
    {
        var cacheA = CreateCache(keyPrefix: "iso-a:");
        var cacheB = CreateCache(keyPrefix: "iso-b:");

        var entryA = new MfaPendingEntry
        {
            UserId = "user-A",
            TenantId = "tenant-A",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        await cacheA.StoreAsync("shared-token", entryA, CancellationToken.None);
        var readFromB = await cacheB.TakeAsync("shared-token", CancellationToken.None);

        readFromB.Should().BeNull(because: "a different KeyPrefix must not see cache-A's entries");

        // Original still retrievable under cache-A
        var readFromA = await cacheA.TakeAsync("shared-token", CancellationToken.None);
        readFromA.Should().NotBeNull();
        readFromA!.UserId.Should().Be("user-A");
    }
}
