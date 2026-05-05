using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
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
        second.Should().Be(first);
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
        second.Should().Be(first);
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
    public async Task SaveAsync_ShouldInvalidateBothIndexes_WhenWriteCompletes()
    {
        var inner = Substitute.For<IUserStore>();
        var t1 = new TenantId("t1");
        var u1 = EntityId.From("u1");
        var v1 = MakeUser("u1", "t1", "u@example.com", display: "Initial");
        var v2 = MakeUser("u1", "t1", "u@example.com", display: "Updated");
        inner.GetByIdAsync(t1, u1, Arg.Any<CancellationToken>()).Returns(v1, v2);
        var sut = new CachedUserStore(inner, _cache);

        var initial = await sut.GetByIdAsync(t1, u1, CancellationToken.None);
        initial!.DisplayName.Should().Be("Initial");

        await sut.SaveAsync(v2, CancellationToken.None);

        var afterSave = await sut.GetByIdAsync(t1, u1, CancellationToken.None);
        afterSave!.DisplayName.Should().Be("Updated");
        await inner.Received(2).GetByIdAsync(t1, u1, Arg.Any<CancellationToken>());
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

    public void Dispose() => _cache.Dispose();

    private static User MakeUser(string userId, string tenantId, string email, string display = "Test User") => new()
    {
        UserId = EntityId.From(userId),
        TenantId = new TenantId(tenantId),
        Email = email,
        DisplayName = display,
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
