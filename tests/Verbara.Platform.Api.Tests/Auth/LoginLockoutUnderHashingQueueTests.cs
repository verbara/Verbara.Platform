using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Wrong-password sign-ins for one account that queue behind the <see cref="PasswordHashingGate"/> stop at the
/// lockout threshold (openspec <c>lockout-under-hashing-queue</c>). Each sign-in reads the account before it waits
/// for a hashing slot; with the lockout checked only on that copy and the failure recorded after the hash, every
/// queued sign-in had passed the check before the first failure was recorded, so each one got its password
/// checked and the account never answered 423 to the burst.
/// </summary>
public sealed class LoginLockoutUnderHashingQueueTests
{
    private const int Burst = 50;
    private const int Threshold = 5;
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task Login_ShouldCheckAtMostThresholdPasswords_WhenABurstQueuesBehindAGateOfOne()
    {
        using var harness = new Harness(gateSize: 1);

        var results = await harness.QueuedWrongPasswordBurstAsync();

        results.Count(r => r is UnauthorizedHttpResult).Should().Be(Threshold,
            "each 401 is one password checked; the threshold-th failure locks the account");
        results.Count(IsLocked).Should().Be(Burst - Threshold,
            "a sign-in that obtains its slot after the lock answers 423 without checking the password");
        (await harness.StoredAsync()).FailedLoginAttempts.Should().Be(Threshold);
        harness.Gate.AvailableSlots.Should().Be(1);
    }

    [Fact]
    public async Task Login_ShouldStopNearTheThreshold_WhenABurstQueuesBehindAWiderGate()
    {
        using var harness = new Harness(gateSize: 2);

        var results = await harness.QueuedWrongPasswordBurstAsync();

        // Sign-ins hashing side by side read the lock before either records its failure, so up to
        // (slots - 1) passwords beyond the threshold are checked; no more.
        results.Count(r => r is UnauthorizedHttpResult).Should().BeInRange(Threshold, Threshold + 1);
        results.Count(IsLocked).Should().Be(Burst - results.Count(r => r is UnauthorizedHttpResult));
        harness.Gate.AvailableSlots.Should().Be(2);
    }

    [Fact]
    public async Task Login_ShouldSignIn_WhenTheRightPasswordWaitedBehindFailuresBelowTheThreshold()
    {
        using var harness = new Harness(gateSize: 1);
        for (var i = 0; i < Threshold - 1; i++)
            (await harness.LoginAsync("not-the-password")).Should().BeOfType<UnauthorizedHttpResult>();

        (await harness.LoginAsync(AuthHandlerFixture.Password)).Should().BeOfType<Ok<TokenResponse>>();

        var stored = await harness.StoredAsync();
        stored.FailedLoginAttempts.Should().Be(0, "a successful sign-in still resets the count");
        stored.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Login_ShouldAnswerLocked_WhenTheAccountLockedWhileTheSignInWaitedForItsSlot()
    {
        using var harness = new Harness(gateSize: 1);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() => harness.Gate.RunAsync(() => release.Wait(Deadline), CancellationToken.None));
        await WaitUntilAsync(() => harness.Gate.AvailableSlots == 0);

        var login = Task.Run(() => harness.LoginAsync(AuthHandlerFixture.Password));
        await WaitUntilAsync(() => harness.EmailReads == 1);
        await Task.Delay(Settle); // fence-allow: SETTLE — let the sign-in pass its first lock check and park on the gate
        await harness.LockAsync();

        release.Set();
        (await login.WaitAsync(Deadline)).Should().Match<IResult>(r => IsLocked(r),
            "the lock set while the sign-in waited is read again once it holds a slot");
        await holder.WaitAsync(Deadline);
    }

    private static bool IsLocked(IResult result) =>
        result is IStatusCodeHttpResult { StatusCode: 423 };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Deadline);
        while (!condition())
            await Task.Delay(10, cts.Token); // fence-allow: LOOP-DRIVER — bounded poll on a counter that raises no signal of its own
    }

    /// <summary>
    /// The sign-in handler over an <see cref="InMemoryUserStore"/> (fresh copies on every read, atomic failure
    /// count), behind a counting facade so a test can tell when every sign-in of a burst has read the account.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly AuthHandlerFixture _fixture;
        private readonly InMemoryUserStore _inner = new();
        private readonly IUserStore _store = Substitute.For<IUserStore>();
        private readonly AccountLockoutService _lockout;
        private readonly TenantId _tenant = new(AuthHandlerFixture.TenantId);
        private readonly EntityId _userId = EntityId.From(AuthHandlerFixture.UserId);
        private int _emailReads;

        public Harness(int gateSize)
        {
            Gate = new PasswordHashingGate(gateSize);
            _fixture = new AuthHandlerFixture()
                .WithUser(mfaEnabled: false, role: UserRole.Agent)
                .WithTenantPolicy(new TenantAuthConfig
                {
                    TenantId = AuthHandlerFixture.TenantId,
                    MfaPolicy = "optional",
                    LockoutThreshold = Threshold,
                    LockoutDurationMinutes = 15,
                });
            _inner.CreateAsync(_fixture.User.Clone(), CancellationToken.None).GetAwaiter().GetResult();

            _store.GetByEmailAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Interlocked.Increment(ref _emailReads);
                    return _inner.GetByEmailAsync(ci.ArgAt<TenantId>(0), ci.ArgAt<string>(1), ci.ArgAt<CancellationToken>(2));
                });
            _store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                .Returns(ci => _inner.GetByIdAsync(ci.ArgAt<TenantId>(0), ci.ArgAt<EntityId>(1), ci.ArgAt<CancellationToken>(2)));
            _store.RecordFailedSignInAsync(
                    Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(ci => _inner.RecordFailedSignInAsync(
                    ci.ArgAt<TenantId>(0), ci.ArgAt<EntityId>(1), ci.ArgAt<int>(2), ci.ArgAt<DateTimeOffset>(3),
                    ci.ArgAt<CancellationToken>(4)));
            _store.ResetLockoutAsync(
                    Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ci => _inner.ResetLockoutAsync(
                    ci.ArgAt<TenantId>(0), ci.ArgAt<EntityId>(1), ci.ArgAt<DateTimeOffset>(2), ci.ArgAt<bool>(3),
                    ci.ArgAt<CancellationToken>(4)));
            _store.SetLastLoginAtAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(ci => _inner.SetLastLoginAtAsync(
                    ci.ArgAt<TenantId>(0), ci.ArgAt<EntityId>(1), ci.ArgAt<DateTimeOffset>(2), ci.ArgAt<CancellationToken>(3)));

            _lockout = new AccountLockoutService(_store, _fixture.ConfigStore, _fixture.AuthEvents);
        }

        public PasswordHashingGate Gate { get; }

        public int EmailReads => Volatile.Read(ref _emailReads);

        public Task<User> StoredAsync() => _inner.GetByIdAsync(_tenant, _userId, CancellationToken.None)!;

        /// <summary>Records failures until the account is locked, as concurrent sign-ins elsewhere would.</summary>
        public async Task LockAsync()
        {
            for (var i = 0; i < Threshold; i++)
                await _inner.RecordFailedSignInAsync(_tenant, _userId, Threshold, DateTimeOffset.UtcNow.AddMinutes(15), default);
        }

        /// <summary>
        /// Holds the gate's slots, starts <see cref="Burst"/> wrong-password sign-ins, waits until every one has read
        /// the account (so each passed the lock check made before the wait), then frees the slots.
        /// </summary>
        public async Task<IResult[]> QueuedWrongPasswordBurstAsync()
        {
            using var release = new ManualResetEventSlim(false);
            var holders = Enumerable.Range(0, Gate.MaxConcurrency)
                .Select(_ => Task.Run(() => Gate.RunAsync(() => release.Wait(Deadline), CancellationToken.None)))
                .ToArray();
            await WaitUntilAsync(() => Gate.AvailableSlots == 0);

            var burst = Enumerable.Range(0, Burst).Select(_ => Task.Run(() => LoginAsync("not-the-password"))).ToArray();
            await WaitUntilAsync(() => EmailReads == Burst);
            await Task.Delay(Settle); // fence-allow: SETTLE — let the last sign-ins pass their first lock check and park on the gate

            release.Set();
            await Task.WhenAll(holders).WaitAsync(Deadline);
            return await Task.WhenAll(burst).WaitAsync(Deadline);
        }

        public Task<IResult> LoginAsync(string password)
        {
            var context = AuthHandlerFixture.BuildHttpContext();
            context.RequestServices = new ServiceCollection().AddSingleton(Gate).BuildServiceProvider();
            return AuthEndpoints.Login(
                new LoginRequest(AuthHandlerFixture.TenantId, AuthHandlerFixture.Email, password),
                context,
                _store,
                _lockout,
                _fixture.JwtService,
                _fixture.RefreshService,
                _fixture.AuthEvents,
                _fixture.ConfigStore,
                _fixture.MfaPolicyEvaluator,
                _fixture.MfaPendingCache,
                authWriteQueue: null,
                _fixture.TenantStore,
                CancellationToken.None);
        }

        public void Dispose() => Gate.Dispose();
    }
}
