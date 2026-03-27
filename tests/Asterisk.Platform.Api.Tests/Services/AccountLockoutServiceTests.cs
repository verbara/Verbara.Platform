using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Services;

public sealed class AccountLockoutServiceTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly ITenantAuthConfigStore _configStore = Substitute.For<ITenantAuthConfigStore>();
    private readonly IAuthEventStore _eventStore = Substitute.For<IAuthEventStore>();
    private readonly AccountLockoutService _sut;

    public AccountLockoutServiceTests()
    {
        var authEvents = new AuthEventService(_eventStore);
        _sut = new AccountLockoutService(_userStore, _configStore, authEvents);
    }

    private static User CreateUser() => new()
    {
        UserId = EntityId.From("u1"),
        TenantId = new TenantId("t1"),
        Email = "user@test.com",
        DisplayName = "Test User",
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task RecordFailedAttemptAsync_ShouldIncrementCounter()
    {
        var user = CreateUser();
        _configStore.GetAsync("t1", Arg.Any<CancellationToken>())
            .Returns(new TenantAuthConfig { TenantId = "t1", LockoutThreshold = 5 });

        await _sut.RecordFailedAttemptAsync(user, null, null, CancellationToken.None);

        user.FailedLoginAttempts.Should().Be(1);
        user.LockedUntil.Should().BeNull();
        await _userStore.Received(1).SaveAsync(user, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordFailedAttemptAsync_ShouldLock_WhenThresholdReached()
    {
        var user = CreateUser();
        user.FailedLoginAttempts = 4; // one more will hit threshold of 5
        _configStore.GetAsync("t1", Arg.Any<CancellationToken>())
            .Returns(new TenantAuthConfig { TenantId = "t1", LockoutThreshold = 5, LockoutDurationMinutes = 15 });

        await _sut.RecordFailedAttemptAsync(user, "1.2.3.4", "Agent", CancellationToken.None);

        user.FailedLoginAttempts.Should().Be(5);
        user.LockedUntil.Should().NotBeNull();
        user.LockedUntil.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(5));
        await _eventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e.EventType == AuthEventTypes.Lockout),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetAttemptsAsync_ShouldClearCounterAndLockout()
    {
        var user = CreateUser();
        user.FailedLoginAttempts = 3;
        user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(10);

        await _sut.ResetAttemptsAsync(user, CancellationToken.None);

        user.FailedLoginAttempts.Should().Be(0);
        user.LockedUntil.Should().BeNull();
        await _userStore.Received(1).SaveAsync(user, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlockAsync_ShouldClearLockoutAndLogEvent()
    {
        var user = CreateUser();
        user.FailedLoginAttempts = 5;
        user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        _userStore.GetByIdAsync(new TenantId("t1"), EntityId.From("u1"), Arg.Any<CancellationToken>())
            .Returns(user);

        await _sut.UnlockAsync(new TenantId("t1"), EntityId.From("u1"), "10.0.0.1", "AdminAgent", CancellationToken.None);

        user.FailedLoginAttempts.Should().Be(0);
        user.LockedUntil.Should().BeNull();
        await _userStore.Received(1).SaveAsync(user, Arg.Any<CancellationToken>());
        await _eventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e.EventType == "admin_unlock"),
            Arg.Any<CancellationToken>());
    }
}
