using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// <see cref="AccountLockoutService"/> leaves the counting to the store: it asks for one failed
/// attempt to be added (with the tenant's threshold and lock duration) and takes the count and lock
/// the store returns, never a count of its own from the snapshot it was handed.
/// </summary>
public sealed class AccountLockoutServiceTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId UserId = EntityId.From("u1");

    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly ITenantAuthConfigStore _configStore = Substitute.For<ITenantAuthConfigStore>();
    private readonly IAuthEventStore _eventStore = Substitute.For<IAuthEventStore>();
    private readonly AccountLockoutService _sut;

    public AccountLockoutServiceTests()
    {
        var authEvents = new AuthEventService(_eventStore);
        _sut = new AccountLockoutService(_userStore, _configStore, authEvents);
        _configStore.GetAsync("t1", Arg.Any<CancellationToken>())
            .Returns(new TenantAuthConfig { TenantId = "t1", LockoutThreshold = 5, LockoutDurationMinutes = 15 });
    }

    private static User CreateUser() => new()
    {
        UserId = UserId,
        TenantId = Tenant,
        Email = "user@test.com",
        DisplayName = "Test User",
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task RecordFailedAttemptAsync_ShouldAskTheStoreToCountTheAttempt_WithTheTenantThresholdAndLockDuration()
    {
        StoreRecords(new FailedSignInResult(1, null));

        await _sut.RecordFailedAttemptAsync(CreateUser(), null, null, CancellationToken.None);

        await _userStore.Received(1).RecordFailedSignInAsync(
            Tenant, UserId, 5,
            Arg.Is<DateTimeOffset>(d => Math.Abs((d - DateTimeOffset.UtcNow.AddMinutes(15)).TotalSeconds) < 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordFailedAttemptAsync_ShouldTakeTheStoredCount_WhenOtherAttemptsLandedSinceTheSnapshotWasRead()
    {
        var user = CreateUser(); // read with 0 failed attempts
        StoreRecords(new FailedSignInResult(3, null));

        await _sut.RecordFailedAttemptAsync(user, null, null, CancellationToken.None);

        user.FailedLoginAttempts.Should().Be(3, because: "the snapshot reflects what the store holds, not its own increment");
        user.LockedUntil.Should().BeNull();
        await _eventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task RecordFailedAttemptAsync_ShouldLogLockout_WhenTheStoredCountReachesTheThreshold()
    {
        var user = CreateUser();
        var lockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        StoreRecords(new FailedSignInResult(5, lockedUntil));

        await _sut.RecordFailedAttemptAsync(user, "1.2.3.4", "Agent", CancellationToken.None);

        user.FailedLoginAttempts.Should().Be(5);
        user.LockedUntil.Should().Be(lockedUntil);
        await _eventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e != null && e.EventType == AuthEventTypes.Lockout),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordFailedAttemptAsync_ShouldChangeNothing_WhenTheUserNoLongerExists()
    {
        var user = CreateUser();
        StoreRecords(null);

        await _sut.RecordFailedAttemptAsync(user, null, null, CancellationToken.None);

        user.FailedLoginAttempts.Should().Be(0);
        await _eventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task ResetAttemptsAsync_ShouldResetTheStoreWithoutLiftingALockInForce_WhenNoQueueIsRegistered()
    {
        var user = CreateUser();
        user.FailedLoginAttempts = 3;

        await _sut.ResetAttemptsAsync(user, CancellationToken.None);

        user.FailedLoginAttempts.Should().Be(0);
        user.LockedUntil.Should().BeNull();
        await _userStore.Received(1).ResetLockoutAsync(
            Tenant, UserId, Arg.Any<DateTimeOffset>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueLastLoginAtUpdateAsync_ShouldWriteTheTime_WhenNoQueueIsRegistered()
    {
        var user = CreateUser();
        var at = DateTimeOffset.UtcNow;

        await _sut.EnqueueLastLoginAtUpdateAsync(user, at, CancellationToken.None);

        user.LastLoginAt.Should().Be(at);
        await _userStore.Received(1).SetLastLoginAtAsync(Tenant, UserId, at, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlockAsync_ShouldLiftTheLockAndLogEvent_WhenTheUserExists()
    {
        _userStore.ResetLockoutAsync(Tenant, UserId, Arg.Any<DateTimeOffset>(), false, Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.UnlockAsync(Tenant, UserId, "10.0.0.1", "AdminAgent", CancellationToken.None);

        await _userStore.Received(1).ResetLockoutAsync(
            Tenant, UserId, Arg.Any<DateTimeOffset>(), false, Arg.Any<CancellationToken>());
        await _eventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e != null && e.EventType == "admin_unlock"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlockAsync_ShouldLogNothing_WhenTheUserDoesNotExist()
    {
        await _sut.UnlockAsync(Tenant, UserId, "10.0.0.1", "AdminAgent", CancellationToken.None);

        await _eventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    private void StoreRecords(FailedSignInResult? result) =>
        _userStore.RecordFailedSignInAsync(Tenant, UserId, Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(result);
}
