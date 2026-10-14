using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Identity.Tests.Mfa;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Verbara.Platform.Identity.Redis.Tests;

/// <summary>
/// Integration tests for <see cref="RedisMfaPendingCache"/> on a real Redis. The behavioural contract is
/// the shared <see cref="MfaPendingCacheContractTests"/> (linked from <c>Verbara.Platform.Identity.Tests</c>,
/// so the in-memory cache passes the very same assertions); its concurrency cases race two caches on two
/// <see cref="ConnectionMultiplexer"/>s, standing in for two API replicas. The cases below cover what only
/// Redis has: real TTL expiry, the key layout and its TTL, key-prefix isolation, and the old key namespace.
/// A per-test random <see cref="RedisIdentityOptions.KeyPrefix"/> keeps runs independent without FLUSHDB.
/// </summary>
[Collection("Redis")]
public sealed class RedisMfaPendingCacheTests : MfaPendingCacheContractTests, IAsyncLifetime
{
    private readonly RedisFixture _fixture;
    private readonly string _prefix = $"test:{Guid.NewGuid():N}:identity:";
    private ConnectionMultiplexer? _secondReplica;

    public RedisMfaPendingCacheTests(RedisFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() =>
        _secondReplica = await ConnectionMultiplexer.ConnectAsync(_fixture.ConnectionString);

    public async Task DisposeAsync()
    {
        if (_secondReplica is not null)
            await _secondReplica.DisposeAsync();
    }

    protected override IMfaPendingCache CreateCache() => CreateCache(_prefix);

    protected override IReadOnlyList<IMfaPendingCache> CreateRacingCaches() =>
    [
        CreateCache(_prefix),
        new RedisMfaPendingCache(_secondReplica!, Options.Create(new RedisIdentityOptions { KeyPrefix = _prefix })),
    ];

    private RedisMfaPendingCache CreateCache(string keyPrefix) =>
        new(_fixture.Redis, Options.Create(new RedisIdentityOptions { KeyPrefix = keyPrefix }));

    private IDatabase Db => _fixture.Redis.GetDatabase();

    [Fact]
    public async Task TryBeginAttemptAsync_ShouldRoundTripEntry_WhenStored()
    {
        var cache = CreateCache();
        var entry = new MfaPendingEntry
        {
            UserId = "user-1",
            TenantId = "tenant-a",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        await cache.StoreAsync("token-abc", entry, CancellationToken.None);
        var result = await cache.TryBeginAttemptAsync("token-abc", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Entry.UserId.Should().Be(entry.UserId);
        result.Entry.TenantId.Should().Be(entry.TenantId);
        result.Entry.ExpiresAt.Should().BeCloseTo(entry.ExpiresAt, TimeSpan.FromSeconds(1));
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
        await Task.Delay(TimeSpan.FromMilliseconds(750)); // fence-allow: SETTLE — wait for real Redis TTL expiry (integration, CI-excluded)
        var result = await cache.TryBeginAttemptAsync("token-ttl", CancellationToken.None);

        result.Should().BeNull(because: "Redis TTL must evict the entry once ExpiresAt has passed");
    }

    [Fact]
    public async Task StoreAsync_ShouldSetTtlWithinChallengeLifetime_WhenStoredAndAfterReservation()
    {
        var cache = CreateCache();
        var key = $"{_prefix}mfa:challenge:token-key";
        await cache.StoreAsync("token-key", new MfaPendingEntry
        {
            UserId = "user-1",
            TenantId = "tenant-a",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        }, CancellationToken.None);

        var afterStore = await Db.KeyTimeToLiveAsync(key);
        (await Db.KeyTypeAsync(key)).Should().Be(RedisType.Hash);
        await cache.TryBeginAttemptAsync("token-key", CancellationToken.None);
        var afterReservation = await Db.KeyTimeToLiveAsync(key);

        afterStore.Should().NotBeNull();
        afterStore!.Value.Should().BePositive().And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        afterReservation.Should().NotBeNull(because: "a reservation must not strip the challenge's TTL");
        afterReservation!.Value.Should().BePositive().And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task TryBeginAttemptAsync_ShouldIgnoreOldNamespaceKey_WhenPreviousVersionStoredIt()
    {
        var cache = CreateCache();
        // What the one-shot implementation wrote: a JSON string under mfa:pending:.
        await Db.StringSetAsync(
            $"{_prefix}mfa:pending:token-old",
            """{"userId":"user-1","tenantId":"tenant-a","expiresAt":"2099-01-01T00:00:00+00:00"}""",
            TimeSpan.FromMinutes(5));

        var attempt = await cache.TryBeginAttemptAsync("token-old", CancellationToken.None);
        var consumed = await cache.TryConsumeAsync("token-old", CancellationToken.None);

        attempt.Should().BeNull(because: "the old namespace is not read — no WRONGTYPE error, just an unknown challenge");
        consumed.Should().BeFalse();
    }

    [Fact]
    public async Task KeyPrefix_ShouldIsolateTenants_WhenSamePrefixWouldCollide()
    {
        var cacheA = CreateCache(keyPrefix: $"iso-a:{Guid.NewGuid():N}:");
        var cacheB = CreateCache(keyPrefix: $"iso-b:{Guid.NewGuid():N}:");

        await cacheA.StoreAsync("shared-token", new MfaPendingEntry
        {
            UserId = "user-A",
            TenantId = "tenant-A",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        }, CancellationToken.None);
        var readFromB = await cacheB.TryBeginAttemptAsync("shared-token", CancellationToken.None);

        readFromB.Should().BeNull(because: "a different KeyPrefix must not see cache-A's entries");

        var readFromA = await cacheA.TryBeginAttemptAsync("shared-token", CancellationToken.None);
        readFromA.Should().NotBeNull();
        readFromA!.Entry.UserId.Should().Be("user-A");
        readFromA.Attempt.Should().Be(1, because: "cache-B's miss spent no attempt on cache-A's challenge");
    }
}
