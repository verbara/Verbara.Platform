using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// AHH Phase 2 — bounded background queue for the success-side login writes.
/// Verifies enqueue + drop semantics, batch coalesce by user, graceful
/// shutdown drain, that <c>auth_events</c> inserts are processed
/// independently of <c>users</c> writes, and that each deferred user write is a
/// targeted store write that cannot undo a change made after the login.
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

        // One targeted write per kind for the user, no read of the whole user, and one
        // auth_events insert.
        await userStore.Received(1).ResetLockoutAsync(
            new TenantId("t1"), EntityId.From("u1"), Arg.Any<DateTimeOffset>(), true, Arg.Any<CancellationToken>());
        await userStore.Received(1).SetLastLoginAtAsync(
            new TenantId("t1"), EntityId.From("u1"), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await userStore.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default, default);
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

        // One last-login write carrying the latest time, and one reset, for all four commands.
        await userStore.ReceivedWithAnyArgs(1).SetLastLoginAtAsync(default, default, default, default);
        await userStore.Received(1).SetLastLoginAtAsync(
            new TenantId("t1"), EntityId.From("u1"), third, Arg.Any<CancellationToken>());
        await userStore.ReceivedWithAnyArgs(1).ResetLockoutAsync(default, default, default, default, default);
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

        // Three distinct (tenant, user) groups → three writes, one per user.
        await userStore.Received(1).SetLastLoginAtAsync(new TenantId("t1"), EntityId.From("userA"), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await userStore.Received(1).SetLastLoginAtAsync(new TenantId("t1"), EntityId.From("userB"), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await userStore.Received(1).SetLastLoginAtAsync(new TenantId("t2"), EntityId.From("userA"), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldKeepProcessing_WhenTheStoreRefusesAUserWrite()
    {
        // The user is gone (or a precondition failed): the store writes nothing and says so. The
        // other kinds and the auth_events insert still go through.
        var (userStore, authEventStore, services) = NewStubStores();
        userStore.SetLastLoginAtAsync(default, default, default, default).ReturnsForAnyArgs(false);
        using var sut = NewQueue(services: services);

        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "ghost", DateTimeOffset.UtcNow));
        sut.TryEnqueue(new ResetLockoutCountersCommand("t1", "ghost"));
        sut.TryEnqueue(new LogSuccessEventCommand("t1", "ghost", "login_success", null, null));

        await StartAndDrain(sut);

        await userStore.ReceivedWithAnyArgs(1).ResetLockoutAsync(default, default, default, default, default);
        await authEventStore.Received(1).SaveAsync(Arg.Any<AuthEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldStillApplyTheOtherWrites_WhenOneUserWriteThrows()
    {
        var (userStore, _, services) = NewStubStores();
        userStore.SetLastLoginAtAsync(default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<bool>(new InvalidOperationException("simulated failure")));
        using var sut = NewQueue(services: services);

        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u1", DateTimeOffset.UtcNow));
        sut.TryEnqueue(new ResetLockoutCountersCommand("t1", "u1"));

        await StartAndDrain(sut);

        await userStore.ReceivedWithAnyArgs(1).ResetLockoutAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Consumer_ShouldUpdatePasswordHash_WhenPasswordRehashCommandEnqueued()
    {
        // AHH Phase 4 — the on-login rehash flow enqueues PasswordRehashCommand
        // after a successful BCrypt verify. The consumer replaces the hash the
        // login verified, and only that hash.
        var (userStore, _, services) = NewStubStores();
        using var sut = NewQueue(services: services);

        const string verifiedBcryptHash = "$2a$12$VERIFIED_HASH_PLACEHOLDER";
        const string newArgon2idHash = "$argon2id$v=19$m=19456,t=2,p=1$NEW_HASH_PLACEHOLDER";
        sut.TryEnqueue(new PasswordRehashCommand("t1", "u1", verifiedBcryptHash, newArgon2idHash));

        await StartAndDrain(sut);

        await userStore.Received(1).RehashPasswordAsync(
            new TenantId("t1"), EntityId.From("u1"), verifiedBcryptHash, newArgon2idHash, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldCoalesceRehashAndLastLogin_WhenSameUserHasBoth()
    {
        var (userStore, _, services) = NewStubStores();
        using var sut = NewQueue(services: services);

        var loginAt = DateTimeOffset.UtcNow;
        const string oldHash = "$2a$12$OLD";
        const string newHash = "$argon2id$v=19$m=19456,t=2,p=1$RESH";
        sut.TryEnqueue(new PasswordRehashCommand("t1", "u1", oldHash, newHash));
        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u1", loginAt));
        sut.TryEnqueue(new ResetLockoutCountersCommand("t1", "u1"));

        await StartAndDrain(sut);

        // One write per kind covering all three mutations.
        await userStore.Received(1).RehashPasswordAsync(
            new TenantId("t1"), EntityId.From("u1"), oldHash, newHash, Arg.Any<CancellationToken>());
        await userStore.Received(1).SetLastLoginAtAsync(
            new TenantId("t1"), EntityId.From("u1"), loginAt, Arg.Any<CancellationToken>());
        await userStore.ReceivedWithAnyArgs(1).ResetLockoutAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Consumer_ShouldDrainPendingItems_OnGracefulShutdown()
    {
        var (_, authEventStore, services) = NewStubStores();

        // Counting barrier: release once all 5 independent auth_events rows have
        // been persisted, so we trigger shutdown AFTER the work is observably
        // done — not after a wall-clock Task.Delay guess (the old barrier flaked
        // under CI load). Log events are not coalesced, so the store-call count
        // equals the enqueue count (5).
        var remaining = 5;
        var allPersisted = new TaskCompletionSource();
        authEventStore.SaveAsync(Arg.Any<AuthEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                    allPersisted.TrySetResult();
                return Task.CompletedTask;
            });
        using var sut = NewQueue(services: services, flushInterval: TimeSpan.FromSeconds(5));

        // Many items enqueued before StartAsync runs the consumer.
        for (var i = 0; i < 5; i++)
        {
            sut.TryEnqueue(new LogSuccessEventCommand("t1", $"u{i}", "login_success", null, null));
        }

        var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        await allPersisted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // all 5 persisted
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);

        // All 5 events must have been persisted before StopAsync returns.
        await authEventStore.Received(5).SaveAsync(Arg.Any<AuthEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_ShouldNotDoubleWrite_WhenBatchInterruptedByCancellationMidProcessing()
    {
        // Regression: graceful shutdown must not re-process an already-handled batch.
        // A real store (Npgsql) throws OperationCanceledException when its write is
        // cancelled mid-batch on shutdown. The consumer must NOT re-run the
        // partially-processed batch in its drain: auth_events INSERTs are
        // non-idempotent (new Guid per row), so re-processing duplicates rows.
        var (_, authEventStore, services) = NewStubStores();
        // Raised once the consumer's MAIN-LOOP batch has persisted u_ok, so the test
        // deterministically reaches the mid-batch failure (rather than the drain-only
        // path that runs when shutdown pre-empts the main loop entirely).
        var okPersisted = new TaskCompletionSource();
        authEventStore
            .SaveAsync(Arg.Is<AuthEvent>(e => e != null && e.UserId == "u_ok"), Arg.Any<CancellationToken>())
            .Returns(_ => { okPersisted.TrySetResult(); return Task.CompletedTask; });
        authEventStore
            .SaveAsync(Arg.Is<AuthEvent>(e => e != null && e.UserId == "u_cancel"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));
        using var sut = NewQueue(services: services, flushInterval: TimeSpan.FromSeconds(5));

        // u_ok is persisted first; u_cancel then throws OCE inside the same batch.
        sut.TryEnqueue(new LogSuccessEventCommand("t1", "u_ok", "login_success", null, null));
        sut.TryEnqueue(new LogSuccessEventCommand("t1", "u_cancel", "login_success", null, null));

        await sut.StartAsync(CancellationToken.None);
        await okPersisted.Task;   // main-loop batch has run and hit the mid-batch OCE
        await sut.StopAsync(CancellationToken.None);

        // The already-persisted event must be written EXACTLY once (no drain re-run).
        await authEventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e != null && e.UserId == "u_ok"), Arg.Any<CancellationToken>());
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

    // ─── Against a real store: what the deferred writes may not undo ───────

    [Fact]
    public async Task Consumer_ShouldKeepTheChangedPassword_WhenARehashQueuedBeforeAPasswordChangeDrainsAfterIt()
    {
        // Login verified the old BCrypt password and queued its Argon2id rehash; the owner changed
        // the password before the queue drained (at least one flush interval later).
        var store = new InMemoryUserStore();
        var user = MakeUser("u-rehash", "t1");
        user.PasswordHash = "$2a$12$hash-of-the-old-password";
        await store.CreateAsync(user, CancellationToken.None);
        using var sut = NewQueue(services: ServicesWith(store));
        sut.TryEnqueue(new PasswordRehashCommand(
            "t1", "u-rehash", "$2a$12$hash-of-the-old-password", "$argon2id$rehash-of-the-old-password"));

        await store.SetPasswordHashAsync(
            user.TenantId, user.UserId, "$argon2id$hash-of-the-new-password", DateTimeOffset.UtcNow,
            clearLockout: false, expectedCurrentHash: null, CancellationToken.None);
        await StartAndDrain(sut);

        (await store.GetByIdAsync(new TenantId("t1"), EntityId.From("u-rehash"), CancellationToken.None))!
            .PasswordHash.Should().Be("$argon2id$hash-of-the-new-password",
                because: "re-hashing the old password must not undo the password change that followed it");
    }

    [Fact]
    public async Task Consumer_ShouldNotRecreateTheUser_WhenTheUserIsDeletedBeforeTheDrain()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(MakeUser("u-gone", "t1"), CancellationToken.None);
        using var sut = NewQueue(services: ServicesWith(store));
        sut.TryEnqueue(new UpdateLastLoginAtCommand("t1", "u-gone", DateTimeOffset.UtcNow));
        sut.TryEnqueue(new ResetLockoutCountersCommand("t1", "u-gone"));

        await store.DeleteAsync(new TenantId("t1"), EntityId.From("u-gone"), CancellationToken.None);
        await StartAndDrain(sut);

        (await store.GetByIdAsync(new TenantId("t1"), EntityId.From("u-gone"), CancellationToken.None))
            .Should().BeNull(because: "a deferred write for a deleted user writes nothing");
    }

    [Fact]
    public async Task Consumer_ShouldKeepALockSetAfterTheSuccess_WhenTheDeferredResetDrains()
    {
        // The sign-in succeeded and queued its reset; failures that followed reached the threshold and
        // locked the account before the queue drained. The reset must not lift that lock.
        var store = new InMemoryUserStore();
        await store.CreateAsync(MakeUser("u-locked", "t1"), CancellationToken.None);
        using var sut = NewQueue(services: ServicesWith(store));
        sut.TryEnqueue(new ResetLockoutCountersCommand("t1", "u-locked"));

        var lockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        for (var i = 0; i < 5; i++)
            await store.RecordFailedSignInAsync(new TenantId("t1"), EntityId.From("u-locked"), 5, lockedUntil, CancellationToken.None);
        await StartAndDrain(sut);

        var stored = (await store.GetByIdAsync(new TenantId("t1"), EntityId.From("u-locked"), CancellationToken.None))!;
        stored.IsLockedOut(DateTimeOffset.UtcNow).Should().BeTrue();
        stored.FailedLoginAttempts.Should().Be(5);
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private static ServiceProvider ServicesWith(IUserStore userStore)
    {
        var authEventStore = Substitute.For<IAuthEventStore>();
        authEventStore.SaveAsync(Arg.Any<AuthEvent>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton(userStore);
        services.AddSingleton(authEventStore);
        return services.BuildServiceProvider();
    }

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
        userStore.SetLastLoginAtAsync(default, default, default, default).ReturnsForAnyArgs(true);
        userStore.ResetLockoutAsync(default, default, default, default, default).ReturnsForAnyArgs(true);
        userStore.RehashPasswordAsync(default, default, default!, default!, default).ReturnsForAnyArgs(true);
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
    /// Deterministically run the consumer to completion: complete the channel
    /// writer so the reader loop drains every <b>already-enqueued</b> item and
    /// then exits naturally, and await the <c>BackgroundService.ExecuteTask</c>.
    /// The barrier is <b>causal</b> — it observes the loop finishing — not a
    /// wall-clock <c>Task.Delay</c> guess, so it does not flake under CI load.
    /// Callers MUST enqueue all items BEFORE calling. Used by tests that don't
    /// care about graceful-shutdown drain semantics.
    /// </summary>
    private static async Task StartAndDrain(AuthWriteQueue sut)
    {
        await sut.StartAsync(CancellationToken.None);
        sut.CompleteWriter();
        await sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);
    }
}
