using Asterisk.Platform.Identity.Auth;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Identity.Redis.Tests;

/// <summary>
/// Integration tests for <see cref="RedisJtiRevocationCache"/>. Mirrors the
/// behaviour expected from <see cref="InMemoryJtiRevocationCache"/> but verifies it
/// against a real Redis container via <see cref="RedisFixture"/>. Each test uses a
/// random <see cref="RedisIdentityOptions.KeyPrefix"/> so runs are independent without
/// requiring FLUSHDB between them.
/// </summary>
[Collection("Redis")]
public sealed class RedisJtiRevocationCacheTests
{
    private readonly RedisFixture _fixture;

    public RedisJtiRevocationCacheTests(RedisFixture fixture) => _fixture = fixture;

    private RedisJtiRevocationCache CreateCache(string? keyPrefix = null)
    {
        var options = Options.Create(new RedisIdentityOptions
        {
            KeyPrefix = keyPrefix ?? $"test:{Guid.NewGuid():N}:identity:",
        });
        return new RedisJtiRevocationCache(_fixture.Redis, options);
    }

    [Fact]
    public async Task RevokeAsync_ShouldMarkJtiRevoked_WhenExpiresInFuture()
    {
        var cache = CreateCache();
        var jti = $"jti-{Guid.NewGuid():N}";

        await cache.RevokeAsync(jti, DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None);

        var revoked = await cache.IsRevokedAsync(jti, CancellationToken.None);
        revoked.Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_ShouldReturnFalse_WhenJtiUnknown()
    {
        var cache = CreateCache();
        var unknownJti = $"never-revoked-{Guid.NewGuid():N}";

        var revoked = await cache.IsRevokedAsync(unknownJti, CancellationToken.None);

        revoked.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_ShouldNoOp_WhenExpiresAtAlreadyInPast()
    {
        var cache = CreateCache();
        var jti = $"jti-stale-{Guid.NewGuid():N}";

        // Already expired — no key should be written.
        await cache.RevokeAsync(jti, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        var revoked = await cache.IsRevokedAsync(jti, CancellationToken.None);
        revoked.Should().BeFalse(because: "RevokeAsync must skip already-expired tokens to avoid orphan keys");
    }

    [Fact]
    public async Task IsRevokedAsync_ShouldReturnFalse_AfterTtlExpiry()
    {
        var cache = CreateCache();
        var jti = $"jti-ttl-{Guid.NewGuid():N}";

        // Short TTL so the test runs quickly without flake.
        await cache.RevokeAsync(jti, DateTimeOffset.UtcNow.AddMilliseconds(500), CancellationToken.None);

        // Sanity: still revoked immediately.
        (await cache.IsRevokedAsync(jti, CancellationToken.None))
            .Should().BeTrue(because: "entry should be live before TTL elapses");

        await Task.Delay(TimeSpan.FromMilliseconds(750));

        var revokedAfterTtl = await cache.IsRevokedAsync(jti, CancellationToken.None);
        revokedAfterTtl.Should().BeFalse(because: "Redis TTL must evict the entry once ExpiresAt has passed");
    }

    [Fact]
    public async Task KeyPrefix_ShouldIsolate_WhenSameJtiUsedAcrossPrefixes()
    {
        var cacheA = CreateCache(keyPrefix: "iso-jti-a:");
        var cacheB = CreateCache(keyPrefix: "iso-jti-b:");
        const string sharedJti = "shared-jti-value";

        await cacheA.RevokeAsync(sharedJti, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None);

        var seenByB = await cacheB.IsRevokedAsync(sharedJti, CancellationToken.None);
        seenByB.Should().BeFalse(because: "a different KeyPrefix must not see cache-A's revocations");

        var seenByA = await cacheA.IsRevokedAsync(sharedJti, CancellationToken.None);
        seenByA.Should().BeTrue(because: "the original prefix must still see its own entries");
    }
}
