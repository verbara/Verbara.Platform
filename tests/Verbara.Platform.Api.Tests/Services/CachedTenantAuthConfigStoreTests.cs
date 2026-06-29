using Verbara.Platform.Api.Services;
using Verbara.Platform.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// AHH Phase 1 — IMemoryCache decorator over <see cref="ITenantAuthConfigStore"/>.
/// </summary>
public sealed class CachedTenantAuthConfigStoreTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    [Fact]
    public async Task GetAsync_ShouldReturnCachedValue_WhenCacheIsWarm()
    {
        var inner = Substitute.For<ITenantAuthConfigStore>();
        inner.GetAsync("t1", Arg.Any<CancellationToken>()).Returns(MakeConfig("t1"));
        var sut = new CachedTenantAuthConfigStore(inner, _cache);

        var first = await sut.GetAsync("t1", CancellationToken.None);
        var second = await sut.GetAsync("t1", CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().Be(first);
        await inner.Received(1).GetAsync("t1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_ShouldCacheNullResult_WhenInnerReturnsNull()
    {
        var inner = Substitute.For<ITenantAuthConfigStore>();
        inner.GetAsync("missing", Arg.Any<CancellationToken>()).Returns((TenantAuthConfig?)null);
        var sut = new CachedTenantAuthConfigStore(inner, _cache);

        await sut.GetAsync("missing", CancellationToken.None);
        await sut.GetAsync("missing", CancellationToken.None);

        // Null caching prevents thundering-herd of DB reads for unconfigured tenants.
        await inner.Received(1).GetAsync("missing", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_ShouldInvalidateCache_WhenWriteCompletes()
    {
        var inner = Substitute.For<ITenantAuthConfigStore>();
        inner.GetAsync("t1", Arg.Any<CancellationToken>())
            .Returns(MakeConfig("t1"), MakeConfig("t1", lockout: 15));
        var sut = new CachedTenantAuthConfigStore(inner, _cache);

        var initial = await sut.GetAsync("t1", CancellationToken.None);
        initial!.LockoutDurationMinutes.Should().Be(10);

        await sut.SaveAsync(MakeConfig("t1", lockout: 15), CancellationToken.None);

        var afterSave = await sut.GetAsync("t1", CancellationToken.None);
        afterSave!.LockoutDurationMinutes.Should().Be(15);
        await inner.Received(2).GetAsync("t1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_ShouldRespectTtl_WhenItemExpires()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        // Drive the MemoryCache's expiry scanning off the same fake time source so advancing
        // virtual time evicts the entry deterministically — no wall-clock sleep.
        using var clockedCache = new MemoryCache(new MemoryCacheOptions
        {
            Clock = new FakeTimeProviderSystemClock(fakeTimeProvider),
        });
        var inner = Substitute.For<ITenantAuthConfigStore>();
        inner.GetAsync("t1", Arg.Any<CancellationToken>())
            .Returns(MakeConfig("t1"), MakeConfig("t1"));
        var sut = new CachedTenantAuthConfigStore(inner, clockedCache, ttl: TimeSpan.FromMilliseconds(50));

        await sut.GetAsync("t1", CancellationToken.None);
        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(120));
        await sut.GetAsync("t1", CancellationToken.None);

        // The 2nd inner hit proves the 50ms TTL entry was actually evicted after advancing time.
        await inner.Received(2).GetAsync("t1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateTenantAuth_ShouldClearCachedEntry_WhenSinkInterfaceCalled()
    {
        var inner = Substitute.For<ITenantAuthConfigStore>();
        inner.GetAsync("t1", Arg.Any<CancellationToken>()).Returns(MakeConfig("t1"));
        var sut = new CachedTenantAuthConfigStore(inner, _cache);

        // Warm
        _ = await sut.GetAsync("t1", CancellationToken.None);
        // Sink-interface invalidation (Redis pubsub fan-in path)
        sut.InvalidateTenantAuth("t1");
        // Next read must hit the inner store again
        _ = await sut.GetAsync("t1", CancellationToken.None);

        await inner.Received(2).GetAsync("t1", Arg.Any<CancellationToken>());
    }

    public void Dispose() => _cache.Dispose();

    /// <summary>
    /// Adapts the modern <see cref="TimeProvider"/> to the legacy
    /// <see cref="ISystemClock"/> that <see cref="MemoryCacheOptions.Clock"/> consumes,
    /// so a single <see cref="FakeTimeProvider"/> drives MemoryCache expiry scanning.
    /// </summary>
    private sealed class FakeTimeProviderSystemClock : ISystemClock
    {
        private readonly TimeProvider _timeProvider;

        public FakeTimeProviderSystemClock(TimeProvider timeProvider) => _timeProvider = timeProvider;

        public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();
    }

    private static TenantAuthConfig MakeConfig(string tenantId, int lockout = 10) => new()
    {
        TenantId = tenantId,
        MfaPolicy = "optional",
        MfaRequiredRoles = [],
        PasswordMinLength = 12,
        PasswordRequireUppercase = true,
        PasswordRequireNumber = true,
        PasswordRequireSpecial = true,
        LockoutThreshold = 5,
        LockoutDurationMinutes = lockout,
        SessionIdleTimeoutMinutes = 30,
        SessionAbsoluteTimeoutHours = 12,
        OidcEnabled = false,
        OidcAutoCreateUsers = false,
        OidcDefaultRole = "Agent",
        ImpersonationMaxConcurrentSessions = 3,
        ImpersonationAutoTimeoutMinutes = 240,
    };
}
