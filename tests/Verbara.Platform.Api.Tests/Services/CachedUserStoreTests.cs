using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Redis;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// AHH Phase 1 — IMemoryCache decorator over <see cref="IUserStore"/>.
/// Asserts cache-hit behavior, multi-tenant key isolation, and the trust-boundary
/// invariant that PasswordHash is contained inside the in-process cache only
/// (the decorator never broadcasts a hash through the Redis pubsub fan-out).
/// </summary>
public sealed class CachedUserStoreTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    [Fact]
    public async Task GetByEmailAsync_ShouldReturnCachedValue_WhenCacheIsWarm()
    {
        var inner = Substitute.For<IUserStore>();
        var tenantId = new TenantId("t1");
        inner.GetByEmailAsync(tenantId, "u@example.com", Arg.Any<CancellationToken>())
            .Returns(MakeUser("u1", "t1", "u@example.com"));
        var sut = new CachedUserStore(inner, _cache);

        var first = await sut.GetByEmailAsync(tenantId, "u@example.com", CancellationToken.None);
        var second = await sut.GetByEmailAsync(tenantId, "u@example.com", CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeEquivalentTo(first);
        await inner.Received(1).GetByEmailAsync(tenantId, "u@example.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnCachedValue_WhenCacheIsWarm()
    {
        var inner = Substitute.For<IUserStore>();
        var tenantId = new TenantId("t1");
        var userId = EntityId.From("u1");
        inner.GetByIdAsync(tenantId, userId, Arg.Any<CancellationToken>())
            .Returns(MakeUser("u1", "t1", "u@example.com"));
        var sut = new CachedUserStore(inner, _cache);

        var first = await sut.GetByIdAsync(tenantId, userId, CancellationToken.None);
        var second = await sut.GetByIdAsync(tenantId, userId, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeEquivalentTo(first);
        await inner.Received(1).GetByIdAsync(tenantId, userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByEmailAsync_ShouldCoPopulateByIdIndex_WhenInnerReturnsUser()
    {
        var inner = Substitute.For<IUserStore>();
        var tenantId = new TenantId("t1");
        inner.GetByEmailAsync(tenantId, "u@example.com", Arg.Any<CancellationToken>())
            .Returns(MakeUser("u1", "t1", "u@example.com"));
        var sut = new CachedUserStore(inner, _cache);

        // First by-email read populates both indexes.
        await sut.GetByEmailAsync(tenantId, "u@example.com", CancellationToken.None);
        // Subsequent by-id read should hit cache, not the inner store.
        await sut.GetByIdAsync(tenantId, EntityId.From("u1"), CancellationToken.None);

        await inner.Received(1).GetByEmailAsync(tenantId, "u@example.com", Arg.Any<CancellationToken>());
        await inner.DidNotReceive().GetByIdAsync(tenantId, Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByEmailAsync_ShouldNotLeakAcrossTenants_WhenSameEmailInTwoTenants()
    {
        var inner = Substitute.For<IUserStore>();
        var t1 = new TenantId("t1");
        var t2 = new TenantId("t2");
        inner.GetByEmailAsync(t1, "shared@example.com", Arg.Any<CancellationToken>())
            .Returns(MakeUser("u1-tenant1", "t1", "shared@example.com"));
        inner.GetByEmailAsync(t2, "shared@example.com", Arg.Any<CancellationToken>())
            .Returns(MakeUser("u1-tenant2", "t2", "shared@example.com"));
        var sut = new CachedUserStore(inner, _cache);

        var fromT1 = await sut.GetByEmailAsync(t1, "shared@example.com", CancellationToken.None);
        var fromT2 = await sut.GetByEmailAsync(t2, "shared@example.com", CancellationToken.None);

        fromT1!.UserId.Value.Should().Be("u1-tenant1");
        fromT2!.UserId.Value.Should().Be("u1-tenant2");
        // Both inner calls must have happened — multi-tenant cache keys must NOT collide.
        await inner.Received(1).GetByEmailAsync(t1, "shared@example.com", Arg.Any<CancellationToken>());
        await inner.Received(1).GetByEmailAsync(t2, "shared@example.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByEmailAsync_ShouldBeCaseInsensitive_WhenCallerVariesCase()
    {
        var inner = Substitute.For<IUserStore>();
        var t1 = new TenantId("t1");
        inner.GetByEmailAsync(t1, "User@Example.com", Arg.Any<CancellationToken>())
            .Returns(MakeUser("u1", "t1", "User@Example.com"));
        var sut = new CachedUserStore(inner, _cache);

        await sut.GetByEmailAsync(t1, "User@Example.com", CancellationToken.None);
        await sut.GetByEmailAsync(t1, "user@example.com", CancellationToken.None);

        // Case difference must hit cache (Postgres lookup is `lower(email) = lower(@Email)`).
        await inner.Received(1).GetByEmailAsync(t1, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdAsync_ShouldHandOutACopy_WhenTheCacheIsWarm()
    {
        // A request changes the user it read (a failed sign-in, a login's lockout reset); a change made
        // to the cached object itself would reach every other request on this replica unwritten.
        var inner = Substitute.For<IUserStore>();
        var t1 = new TenantId("t1");
        var u1 = EntityId.From("u1");
        inner.GetByIdAsync(t1, u1, Arg.Any<CancellationToken>()).Returns(MakeUser("u1", "t1", "u@example.com"));
        var sut = new CachedUserStore(inner, _cache);

        _ = await sut.GetByIdAsync(t1, u1, CancellationToken.None); // miss: fills both entries
        var fromHit = await sut.GetByIdAsync(t1, u1, CancellationToken.None);
        fromHit!.FailedLoginAttempts = 4;
        fromHit.Status = UserStatus.Suspended;
        var fromEmailHit = await sut.GetByEmailAsync(t1, "u@example.com", CancellationToken.None);
        fromEmailHit!.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        var later = await sut.GetByIdAsync(t1, u1, CancellationToken.None);

        later.Should().NotBeSameAs(fromHit);
        later!.FailedLoginAttempts.Should().Be(0, because: "a change to one request's copy is not a write");
        later.Status.Should().Be(UserStatus.Active);
        later.LockedUntil.Should().BeNull(because: "the by-email entry is not handed out either");
        await inner.Received(1).GetByIdAsync(t1, u1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ShouldDropACachedMiss_WhenTheUserIsCreated()
    {
        var inner = Substitute.For<IUserStore>();
        var t1 = new TenantId("t1");
        var u1 = EntityId.From("u1");
        var created = MakeUser("u1", "t1", "u@example.com");
        inner.GetByIdAsync(t1, u1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(null), Task.FromResult<User?>(created));
        var sut = new CachedUserStore(inner, _cache);
        (await sut.GetByIdAsync(t1, u1, CancellationToken.None)).Should().BeNull(); // caches the miss

        await sut.CreateAsync(created, CancellationToken.None);

        await inner.Received(1).CreateAsync(created, Arg.Any<CancellationToken>());
        (await sut.GetByIdAsync(t1, u1, CancellationToken.None)).Should().NotBeNull(
            because: "a lookup made before the user existed must not hide it for the TTL");
    }

    [Fact]
    public async Task GetByEmailAsync_ShouldNotPersistPasswordHashOutsideMemoryCache_WhenAccessed()
    {
        // Trust-boundary regression: the User object that flows through the cache
        // includes PasswordHash, but the cache implementation MUST be IMemoryCache
        // (in-process). This test asserts the cached object reaches the caller
        // intact and that no string of the hash leaks via the cache key.
        var inner = Substitute.For<IUserStore>();
        var t1 = new TenantId("t1");
        var user = MakeUser("u1", "t1", "u@example.com");
        user.PasswordHash = "$2a$12$SECRET-NOT-LEAKED";
        inner.GetByEmailAsync(t1, "u@example.com", Arg.Any<CancellationToken>()).Returns(user);
        var sut = new CachedUserStore(inner, _cache);

        var fetched = await sut.GetByEmailAsync(t1, "u@example.com", CancellationToken.None);
        fetched!.PasswordHash.Should().Be("$2a$12$SECRET-NOT-LEAKED");

        // Cache-key invariant: must not contain the hash, must contain only the email.
        var key = CachedUserStore.ByEmailKey("t1", "u@example.com");
        key.Should().NotContain("SECRET");
        key.Should().Contain("u@example.com");
    }

    [Fact]
    public async Task InvalidateUser_ShouldClearBothIndexes_WhenSinkInterfaceCalled()
    {
        var inner = Substitute.For<IUserStore>();
        var t1 = new TenantId("t1");
        var u1 = EntityId.From("u1");
        inner.GetByIdAsync(t1, u1, Arg.Any<CancellationToken>())
            .Returns(MakeUser("u1", "t1", "u@example.com"));
        var sut = new CachedUserStore(inner, _cache);

        _ = await sut.GetByIdAsync(t1, u1, CancellationToken.None);
        sut.InvalidateUser("t1", "u1", "u@example.com");
        _ = await sut.GetByIdAsync(t1, u1, CancellationToken.None);

        await inner.Received(2).GetByIdAsync(t1, u1, Arg.Any<CancellationToken>());
    }

    // ─── Every write ─────────────────────────────────────────────────────────

    public static TheoryData<string> Writes => new()
    {
        nameof(IUserStore.UpdateProfileAsync),
        nameof(IUserStore.UpdateAdminFieldsAsync),
        nameof(IUserStore.SetPasswordHashAsync),
        nameof(IUserStore.RehashPasswordAsync),
        nameof(IUserStore.RecordFailedSignInAsync),
        nameof(IUserStore.ResetLockoutAsync),
        nameof(IUserStore.SetLastLoginAtAsync),
        nameof(IUserStore.SetPendingMfaAsync),
        nameof(IUserStore.EnableMfaAsync),
        nameof(IUserStore.ClearMfaAsync),
        nameof(IUserStore.SetRecoveryCodesAsync),
        nameof(IUserStore.ConsumeRecoveryCodeAsync),
        nameof(IUserStore.DeleteAsync),
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ShouldPassThroughAndDropBothIndexes_WhenTheUserIsCached(string write)
    {
        // A password sign-in reads by email: an entry left behind under either key would keep serving
        // the pre-write user (its MFA state, lock, password hash) for up to the TTL.
        var inner = Substitute.For<IUserStore>();
        var t1 = new TenantId("t1");
        var u1 = EntityId.From("u1");
        inner.GetByIdAsync(t1, u1, Arg.Any<CancellationToken>()).Returns(MakeUser("u1", "t1", "u@example.com"));
        var sut = new CachedUserStore(inner, _cache);
        _ = await sut.GetByIdAsync(t1, u1, CancellationToken.None);

        await InvokeAsync(sut, write, t1, u1);

        inner.ReceivedCalls().Should().Contain(c => c.GetMethodInfo().Name == write,
            because: "every write goes to the inner store");
        _cache.TryGetValue(CachedUserStore.ByIdKey("t1", "u1"), out _).Should().BeFalse();
        _cache.TryGetValue(CachedUserStore.ByEmailKey("t1", "u@example.com"), out _).Should().BeFalse(
            because: "the by-email entry holds the same pre-write user");
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ShouldPublishTheInvalidationWithTheEmail_WhenTheUserIsNotCached(string write)
    {
        // Other replicas drop their entries only on this message, and their by-email key needs the
        // email, which no targeted write carries: it comes from the inner store on a miss.
        var inner = Substitute.For<IUserStore>();
        var publisher = Substitute.For<IAuthCachePublisher>();
        var t1 = new TenantId("t1");
        var u1 = EntityId.From("u1");
        inner.GetByIdAsync(t1, u1, Arg.Any<CancellationToken>()).Returns(MakeUser("u1", "t1", "u@example.com"));
        var sut = new CachedUserStore(inner, _cache, publisher);

        await InvokeAsync(sut, write, t1, u1);

        await publisher.Received(1).PublishUserAsync("t1", "u1", "u@example.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldReturnTheInnerResult_WhenTheWriteIsRefused()
    {
        var inner = Substitute.For<IUserStore>();
        var t1 = new TenantId("t1");
        var u1 = EntityId.From("u1");
        inner.UpdateAdminFieldsAsync(t1, u1, Arg.Any<AdminFieldsChange>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(AdminFieldsWriteResult.Stale);
        var sut = new CachedUserStore(inner, _cache);

        var result = await sut.UpdateAdminFieldsAsync(
            t1, u1, new AdminFieldsChange { Role = UserRole.Agent }, DateTimeOffset.UtcNow, "admin", CancellationToken.None);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.Stale);
    }

    private static Task InvokeAsync(CachedUserStore store, string write, TenantId t, EntityId u)
    {
        var now = DateTimeOffset.UtcNow;
        var ct = CancellationToken.None;
        return write switch
        {
            nameof(IUserStore.UpdateProfileAsync) => store.UpdateProfileAsync(t, u, new UserProfileChange { DisplayName = "x" }, now, ct),
            nameof(IUserStore.UpdateAdminFieldsAsync) => store.UpdateAdminFieldsAsync(t, u, new AdminFieldsChange { Status = UserStatus.Suspended }, now, "admin", ct),
            nameof(IUserStore.SetPasswordHashAsync) => store.SetPasswordHashAsync(t, u, "new-hash", now, clearLockout: false, expectedCurrentHash: null, ct),
            nameof(IUserStore.RehashPasswordAsync) => store.RehashPasswordAsync(t, u, "old-hash", "new-hash", ct),
            nameof(IUserStore.RecordFailedSignInAsync) => store.RecordFailedSignInAsync(t, u, 5, now.AddMinutes(15), ct),
            nameof(IUserStore.ResetLockoutAsync) => store.ResetLockoutAsync(t, u, now, onlyIfUnlocked: true, ct),
            nameof(IUserStore.SetLastLoginAtAsync) => store.SetLastLoginAtAsync(t, u, now, ct),
            nameof(IUserStore.SetPendingMfaAsync) => store.SetPendingMfaAsync(t, u, "SECRET", ["digest"], now, ct),
            nameof(IUserStore.EnableMfaAsync) => store.EnableMfaAsync(t, u, now, ct),
            nameof(IUserStore.ClearMfaAsync) => store.ClearMfaAsync(t, u, clearLockout: true, now, ct),
            nameof(IUserStore.SetRecoveryCodesAsync) => store.SetRecoveryCodesAsync(t, u, ["digest"], now, ct),
            nameof(IUserStore.ConsumeRecoveryCodeAsync) => store.ConsumeRecoveryCodeAsync(t, u, "digest", ct),
            nameof(IUserStore.DeleteAsync) => store.DeleteAsync(t, u, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, "not a user-store write"),
        };
    }

    public void Dispose() => _cache.Dispose();

    private static User MakeUser(
        string userId,
        string tenantId,
        string email,
        string display = "Test User",
        UserRole role = UserRole.Agent,
        UserStatus status = UserStatus.Active) => new()
    {
        UserId = EntityId.From(userId),
        TenantId = new TenantId(tenantId),
        Email = email,
        DisplayName = display,
        Role = role,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
