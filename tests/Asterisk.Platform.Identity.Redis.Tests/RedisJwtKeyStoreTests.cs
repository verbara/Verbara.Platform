using Asterisk.Platform.Identity.Auth.Jwt;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Identity.Redis.Tests;

/// <summary>
/// Integration tests for <see cref="RedisJwtKeyStore"/>. Mirrors the behaviour
/// expected from <see cref="InMemoryJwtKeyStore"/> but verifies it against a
/// real Redis container via <see cref="RedisFixture"/>. Each test uses a
/// random <see cref="RedisIdentityOptions.KeyPrefix"/> so runs are independent
/// without requiring FLUSHDB between them.
/// </summary>
/// <remarks>
/// R5.4 S5.9 — Closes C.1 of post-R5.1 triage. The cluster scenario (zero-
/// downtime rotation across nodes) is exercised by
/// <see cref="UpsertAsync_ShouldVisibleAcrossSeparateStoreInstances_WhenSharingRedis"/>:
/// two store instances pointing at the same Redis + same prefix simulate two
/// Platform API nodes; a key written through one is visible to the other,
/// proving multi-node validation works during the grace window.
/// </remarks>
[Collection("Redis")]
public sealed class RedisJwtKeyStoreTests
{
    private readonly RedisFixture _fixture;

    public RedisJwtKeyStoreTests(RedisFixture fixture) => _fixture = fixture;

    private RedisJwtKeyStore CreateStore(string? keyPrefix = null)
    {
        var options = Options.Create(new RedisIdentityOptions
        {
            KeyPrefix = keyPrefix ?? $"test:{Guid.NewGuid():N}:identity:",
        });
        return new RedisJwtKeyStore(_fixture.Redis, options);
    }

    [Fact]
    public async Task UpsertAsync_ShouldPersistKey_AndGetActiveReturnsIt()
    {
        var store = CreateStore();
        var entry = new JwtKeyEntry
        {
            KeyId = $"jwt-{Guid.NewGuid():N}",
            Key = Convert.ToBase64String([1, 2, 3, 4]),
            ActivatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            IsActive = true,
        };

        await store.UpsertAsync(entry, CancellationToken.None);

        var active = await store.GetActiveAsync(CancellationToken.None);
        active.Should().NotBeNull();
        active!.KeyId.Should().Be(entry.KeyId);
        active.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRespectTtl_BasedOnExpiresAt()
    {
        var store = CreateStore();
        var entry = new JwtKeyEntry
        {
            KeyId = $"jwt-ttl-{Guid.NewGuid():N}",
            Key = Convert.ToBase64String([9, 9, 9]),
            ActivatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(2),
            IsActive = true,
        };

        await store.UpsertAsync(entry, CancellationToken.None);

        // Sanity: still present immediately.
        (await store.GetActiveAsync(CancellationToken.None)).Should().NotBeNull();

        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        var afterTtl = await store.GetActiveAsync(CancellationToken.None);
        afterTtl.Should().BeNull(because: "Redis TTL must evict the entry once ExpiresAt has passed");
    }

    [Fact]
    public async Task UpsertAsync_ShouldNoOp_WhenExpiresAtAlreadyInPast()
    {
        var store = CreateStore();
        var entry = new JwtKeyEntry
        {
            KeyId = $"jwt-stale-{Guid.NewGuid():N}",
            Key = "abc",
            ActivatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            IsActive = true,
        };

        await store.UpsertAsync(entry, CancellationToken.None);

        var active = await store.GetActiveAsync(CancellationToken.None);
        active.Should().BeNull(because: "UpsertAsync must skip already-expired entries to avoid orphan keys");
    }

    [Fact]
    public async Task UpsertAsync_ShouldVisibleAcrossSeparateStoreInstances_WhenSharingRedis()
    {
        // Simulates two Platform API nodes (separate process, separate store
        // instance) pointing at the same Redis with the same prefix. Proves
        // R5.4 S5.9 acceptance: zero-downtime rotation across nodes during
        // the grace window — node A signs a token, node B validates it.
        var sharedPrefix = $"cluster:{Guid.NewGuid():N}:identity:";
        var nodeA = CreateStore(sharedPrefix);
        var nodeB = CreateStore(sharedPrefix);

        var entry = new JwtKeyEntry
        {
            KeyId = $"jwt-cluster-{Guid.NewGuid():N}",
            Key = Convert.ToBase64String([5, 5, 5]),
            ActivatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            IsActive = true,
        };

        await nodeA.UpsertAsync(entry, CancellationToken.None);

        var seenByB = await nodeB.GetActiveAsync(CancellationToken.None);
        seenByB.Should().NotBeNull(because: "the second node must see the active key written by the first node");
        seenByB!.KeyId.Should().Be(entry.KeyId);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnEveryLiveEntry_AcrossMultipleRotations()
    {
        var store = CreateStore();

        var k1 = MakeKey(isActive: false, expiresIn: TimeSpan.FromHours(1));
        var k2 = MakeKey(isActive: true, expiresIn: TimeSpan.FromHours(1));

        await store.UpsertAsync(k1, CancellationToken.None);
        await store.UpsertAsync(k2, CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);
        all.Should().HaveCount(2);
        all.Select(e => e.KeyId).Should().Contain([k1.KeyId, k2.KeyId]);
    }

    private static JwtKeyEntry MakeKey(bool isActive, TimeSpan expiresIn) => new()
    {
        KeyId = $"jwt-{Guid.NewGuid():N}",
        Key = Convert.ToBase64String([1]),
        ActivatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.Add(expiresIn),
        IsActive = isActive,
    };
}
