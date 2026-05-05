using Verbara.Platform.Identity.Mfa;

namespace Verbara.Platform.Identity.Tests.Mfa;

/// <summary>
/// Contract tests for <see cref="InMemoryMfaPendingCache"/> and
/// <see cref="InMemoryPasswordResetCache"/>.
/// </summary>
public sealed class InMemoryCacheTests
{
    // ─── IMfaPendingCache ─────────────────────────────────────────────────────

    [Fact]
    public async Task MfaCache_StoreAsync_ShouldMakeEntryRetrievable_WhenCalled()
    {
        var cache = new InMemoryMfaPendingCache();
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
    }

    [Fact]
    public async Task MfaCache_TakeAsync_ShouldReturnNull_WhenKeyMissing()
    {
        var cache = new InMemoryMfaPendingCache();

        var result = await cache.TakeAsync("does-not-exist", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MfaCache_TakeAsync_ShouldRemoveEntry_WhenCalled()
    {
        var cache = new InMemoryMfaPendingCache();
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
    public async Task MfaCache_TakeAsync_ShouldReturnNull_WhenEntryExpired()
    {
        var cache = new InMemoryMfaPendingCache();
        var entry = new MfaPendingEntry
        {
            UserId = "user-1",
            TenantId = "tenant-a",
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(-1),
        };

        await cache.StoreAsync("token-stale", entry, CancellationToken.None);
        var result = await cache.TakeAsync("token-stale", CancellationToken.None);

        result.Should().BeNull(because: "expired entries must not be returned");
    }

    // ─── IPasswordResetCache ──────────────────────────────────────────────────

    [Fact]
    public async Task PasswordResetCache_StoreAsync_ShouldMakeEntryRetrievable_WhenCalled()
    {
        var cache = new InMemoryPasswordResetCache();
        var entry = new PasswordResetEntry
        {
            UserId = "user-2",
            TenantId = "tenant-b",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        await cache.StoreAsync("reset-abc", entry, CancellationToken.None);
        var result = await cache.TakeAsync("reset-abc", CancellationToken.None);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(entry.UserId);
        result.TenantId.Should().Be(entry.TenantId);
    }

    [Fact]
    public async Task PasswordResetCache_TakeAsync_ShouldReturnNull_WhenKeyMissing()
    {
        var cache = new InMemoryPasswordResetCache();

        var result = await cache.TakeAsync("does-not-exist", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PasswordResetCache_TakeAsync_ShouldRemoveEntry_WhenCalled()
    {
        var cache = new InMemoryPasswordResetCache();
        var entry = new PasswordResetEntry
        {
            UserId = "user-2",
            TenantId = "tenant-b",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        await cache.StoreAsync("reset-xyz", entry, CancellationToken.None);
        _ = await cache.TakeAsync("reset-xyz", CancellationToken.None);
        var secondTake = await cache.TakeAsync("reset-xyz", CancellationToken.None);

        secondTake.Should().BeNull(because: "TakeAsync must consume the entry on first call");
    }

    [Fact]
    public async Task PasswordResetCache_TakeAsync_ShouldReturnNull_WhenEntryExpired()
    {
        var cache = new InMemoryPasswordResetCache();
        var entry = new PasswordResetEntry
        {
            UserId = "user-2",
            TenantId = "tenant-b",
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(-1),
        };

        await cache.StoreAsync("reset-stale", entry, CancellationToken.None);
        var result = await cache.TakeAsync("reset-stale", CancellationToken.None);

        result.Should().BeNull(because: "expired entries must not be returned");
    }
}
