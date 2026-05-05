using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// AHH Phase 2 — bounded background queue for the success-side login writes.
/// Verifies enqueue + drop semantics, batch coalesce by user, graceful
/// shutdown drain, and that <c>auth_events</c> inserts are processed
/// independently of <c>users</c> upserts.
/// </summary>
public sealed class AuthWriteQueueTests
{
    [Fact]
    public void TryEnqueue_ShouldReturnTrue_WhenChannelHasCapacity()
    {
        using var sut = NewQueue(capacity: 16);

        var ok = sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u1", DateTimeOffset.UtcNow));

        ok.Should().BeTrue();
    }

    [Fact]
    public void TryEnqueue_ShouldReturnFalse_WhenChannelIsFull()
    {
        // Capacity 2, no consumer (we never call StartAsync). Items pile up
        // and the third TryEnqueue must return false (DropWrite).
        using var sut = NewQueue(capacity: 2);

        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u1", DateTimeOffset.UtcNow));
        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u2", DateTimeOffset.UtcNow));
        var dropped = sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u3", DateTimeOffset.UtcNow));

        dropped.Should().BeFalse();
    }

    [Fact]
    public async Task Consumer_ShouldPersistAllCommands_WhenBatchHasMixedTypes()
    {
        var (userStore, authEventStore, services) = NewStubStores();
        using var sut = NewQueue(services: services);

        sut.TryEnqueue(new ResetLockoutCountersCommand("t1", "u1"));
        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u1", DateTimeOffset.UtcNow));
        sut.TryEnqueue(new LogSuccessEventCommand("t1", "u1", "login_success", "127.0.0.1", "Mozilla/5.0"));

        await StartAndDrain(sut);

        // One DB read + write for the user (coalesced) and one auth_events insert.
        await userStore.Received(1).GetByIdAsync(
            Arg.Is<TenantId>(t => t.Value == "t1"),
            Arg.Is<EntityId>(u => u.Value == "u1"),
            Arg.Any<CancellationToken>());
        await userStore.Received(1).SaveAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await authEventStore.Received(1).SaveAsync(Arg.Any<AuthEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldCoalesceUserMutations_WhenSameUserHasMultipleCommandsInBatch()
    {
        var (userStore, authEventStore, services) = NewStubStores();
        using var sut = NewQueue(services: services);

        // 3 LastLoginAt updates for the same user — last-writer-wins.
        var first = DateTimeOffset.UtcNow;
        var second = first.AddMilliseconds(50);
        var third = first.AddMilliseconds(100);

        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u1", first));
        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u1", second));
        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u1", third));
        sut.TryEnqueue(new ResetLockoutCountersCommand("t1", "u1"));

        await StartAndDrain(sut);

        // One read + one save covering ALL four commands.
        await userStore.Received(1).GetByIdAsync(
            Arg.Is<TenantId>(t => t.Value == "t1"),
            Arg.Is<EntityId>(u => u.Value == "u1"),
            Arg.Any<CancellationToken>());
        await userStore.Received(1).SaveAsync(
            Arg.Is<User>(u =>
                u.LastLoginAt == third &&
                u.FailedLoginAttempts == 0 &&
                u.LockedUntil == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldNotMixUsers_WhenBatchSpansMultipleUsers()
    {
        var (userStore, _, services) = NewStubStores();
        using var sut = NewQueue(services: services);

        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "userA", DateTimeOffset.UtcNow));
        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "userB", DateTimeOffset.UtcNow));
        sut.TryEnqueue(new UpdateLastLoginAtCommand("t2", "userA", DateTimeOffset.UtcNow));

        await StartAndDrain(sut);

        // Three distinct (tenant, user) groups → three reads + three saves.
        await userStore.Received(3).GetByIdAsync(
            Arg.Any<TenantId>(),
            Arg.Any<EntityId>(),
            Arg.Any<CancellationToken>());
        await userStore.Received(3).SaveAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldNotPersistUserMutations_WhenUserIsNotFound()
    {
        var (userStore, authEventStore, services) = NewStubStores();
        // Reconfigure userStore to return null for the lookup.
        userStore.GetByIdAsync(
            Arg.Any<TenantId>(),
            Arg.Any<EntityId>(),
            Arg.Any<CancellationToken>()).Returns(Task.FromResult<User?>(null));
        using var sut = NewQueue(services: services);

        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "ghost", DateTimeOffset.UtcNow));

        await StartAndDrain(sut);

        await userStore.DidNotReceive().SaveAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldUpdatePasswordHash_WhenPasswordRehashCommandEnqueued()
    {
        // AHH Phase 4 — the on-login rehash flow enqueues PasswordRehashCommand
        // after a successful BCrypt verify. The consumer must re-fetch the user,
        // overwrite PasswordHash, and persist via SaveAsync.
        var (userStore, _, services) = NewStubStores();
        using var sut = NewQueue(services: services);

        const string newArgon2idHash = "$argon2id$v=19$m=19456,t=2,p=1$NEW_HASH_PLACEHOLDER";
        sut.TryEnqueue(new PasswordRehashCommand("t1", "u1", newArgon2idHash));

        await StartAndDrain(sut);

        await userStore.Received(1).SaveAsync(
            Arg.Is<User>(u => u.PasswordHash == newArgon2idHash),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldCoalesceRehashAndLastLogin_WhenSameUserHasBoth()
    {
        var (userStore, _, services) = NewStubStores();
        using var sut = NewQueue(services: services);

        var loginAt = DateTimeOffset.UtcNow;
        const string newHash = "$argon2id$v=19$m=19456,t=2,p=1$RESH";
        sut.TryEnqueue(new PasswordRehashCommand("t1", "u1", newHash));
        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u1", loginAt));
        sut.TryEnqueue(new ResetLockoutCountersCommand("t1", "u1"));

        await StartAndDrain(sut);

        // Single read + single save covering all three mutations.
        await userStore.Received(1).GetByIdAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
        await userStore.Received(1).SaveAsync(
            Arg.Is<User>(u =>
                u.PasswordHash == newHash &&
                u.LastLoginAt == loginAt &&
                u.FailedLoginAttempts == 0 &&
                u.LockedUntil == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldDrainPendingItems_OnGracefulShutdown()
    {
        var (_, authEventStore, services) = NewStubStores();
        using var sut = NewQueue(services: services, flushInterval: TimeSpan.FromSeconds(5));

        // Many items enqueued before StartAsync runs the consumer.
        for (var i = 0; i < 5; i++)
        {
            sut.TryEnqueue(new LogSuccessEventCommand("t1", $"u{i}", "login_success", null, null));
        }

        var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        // Brief sleep to let the first batch process; then ask shutdown to drain.
        await Task.Delay(100);
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);

        // All 5 events must have been persisted before StopAsync returns.
        await authEventStore.Received(5).SaveAsync(Arg.Any<AuthEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldContinueProcessing_WhenIndividualCommandFails()
    {
        var (userStore, authEventStore, services) = NewStubStores();
        // Make auth-event saves throw on the first call only.
        authEventStore.SaveAsync(Arg.Any<AuthEvent>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException(new InvalidOperationException("simulated failure")),
                _ => Task.CompletedTask);

        using var sut = NewQueue(services: services);

        sut.TryEnqueue(new LogSuccessEventCommand("t1", "u1", "login_success", null, null));
        sut.TryEnqueue(new LogSuccessEventCommand("t1", "u2", "login_success", null, null));

        await StartAndDrain(sut);

        // Both attempts happened; the second must succeed despite the first failing.
        await authEventStore.Received(2).SaveAsync(Arg.Any<AuthEvent>(), Arg.Any<CancellationToken>());
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private static AuthWriteQueue NewQueue(
        int? capacity = null,
        TimeSpan? flushInterval = null,
        IServiceProvider? services = null)
    {
        services ??= NewStubStores().Services;
        return new AuthWriteQueue(
            services,
            NullLogger<AuthWriteQueue>.Instance,
            capacity: capacity,
            batchSize: 64,
            flushInterval: flushInterval ?? TimeSpan.FromMilliseconds(20));
    }

    private static (IUserStore UserStore, IAuthEventStore AuthEventStore, IServiceProvider Services) NewStubStores()
    {
        var userStore = Substitute.For<IUserStore>();
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<User?>(MakeUser(((EntityId)ci[1]).Value, ((TenantId)ci[0]).Value)));
        userStore.SaveAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var authEventStore = Substitute.For<IAuthEventStore>();
        authEventStore.SaveAsync(Arg.Any<AuthEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(userStore);
        services.AddSingleton(authEventStore);
        return (userStore, authEventStore, services.BuildServiceProvider());
    }

    private static User MakeUser(string userId, string tenantId) => new()
    {
        UserId = EntityId.From(userId),
        TenantId = new TenantId(tenantId),
        Email = $"{userId}@example.com",
        DisplayName = userId,
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Run the consumer until the channel is empty, then stop. Used by tests
    /// that don't care about graceful-shutdown drain semantics.
    /// </summary>
    private static async Task StartAndDrain(AuthWriteQueue sut)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await sut.StartAsync(cts.Token);
        // Give the consumer a couple of flush cycles to process pending items.
        await Task.Delay(100);
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);
    }
}
