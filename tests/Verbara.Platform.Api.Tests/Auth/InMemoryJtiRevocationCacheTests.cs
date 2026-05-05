using Verbara.Platform.Identity.Auth;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests.Auth;

public class InMemoryJtiRevocationCacheTests
{
    [Fact]
    public async Task RevokeAsync_ShouldNotStore_WhenExpiresAtAlreadyInPast()
    {
        var cache = new InMemoryJtiRevocationCache();
        await cache.RevokeAsync("jti-1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        (await cache.IsRevokedAsync("jti-1", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_ShouldPruneEntry_WhenExpiresAtBecomesPast()
    {
        var cache = new InMemoryJtiRevocationCache();
        await cache.RevokeAsync("jti-1", DateTimeOffset.UtcNow.AddMilliseconds(200), CancellationToken.None);

        (await cache.IsRevokedAsync("jti-1", CancellationToken.None)).Should().BeTrue();
        await Task.Delay(250);
        (await cache.IsRevokedAsync("jti-1", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_ShouldReturnTrue_WhenJtiRevokedAndNotExpired()
    {
        var cache = new InMemoryJtiRevocationCache();
        await cache.RevokeAsync("jti-1", DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None);

        (await cache.IsRevokedAsync("jti-1", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_ShouldReturnFalse_WhenJtiNeverRevoked()
    {
        var cache = new InMemoryJtiRevocationCache();

        (await cache.IsRevokedAsync("unknown-jti", CancellationToken.None)).Should().BeFalse();
    }
}
