using System.Security.Claims;
using System.Text.Json;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Identity.OidcTokenExchange;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OtpNet;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// The lifecycle of one <c>mfaToken</c> on <c>POST /auth/mfa/verify</c>
/// (openspec <c>harden-mfa-verify-endpoint</c>, spec requirements "A challenge token SHALL allow three
/// attempts and then be spent" and "A spent or expired challenge SHALL be refused with a stable code").
/// </summary>
/// <remarks>
/// Unlike <see cref="MfaVerifyBookkeepingTests"/>, whose fixture mints a fresh challenge per call, every
/// request here reuses ONE challenge token: the attempt count lives on the challenge. Each result is
/// executed against a real response so the assertions read the status and the <c>error</c> member a client
/// sees, whatever <see cref="IResult"/> type produced them.
/// </remarks>
public sealed class MfaVerifyChallengeAttemptsTests
{
    private const string TenantId = "mfa-attempts-tenant";
    private const string UserId = "mfa-attempts-user";
    private const string ExpiredCode = "mfa-challenge-expired";
    private const string ExhaustedCode = "mfa-challenge-exhausted";

    // ─── Two typos and a correct code ───────────────────────────────────────

    [Fact]
    public async Task MfaVerify_ShouldIssueSession_WhenTwoWrongCodesPrecedeCorrectOne()
    {
        var fixture = new ChallengeFixture();
        var token = await fixture.MintChallengeAsync();

        var first = await fixture.VerifyAsync(token, code: ChallengeFixture.WrongTotp());
        var second = await fixture.VerifyAsync(token, code: ChallengeFixture.WrongTotp());
        var third = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());

        first.Status.Should().Be(401, because: "the first typo is a plain wrong factor");
        first.Error.Should().BeNull(because: "a 401 with the challenge still live carries no challenge code");
        second.Status.Should().Be(401, because: "the challenge survives a second typo");
        second.Error.Should().BeNull(because: "a 401 with the challenge still live carries no challenge code");
        third.Status.Should().Be(200, because: "a correct third factor on a live challenge signs the user in");
        third.Result.Should().BeOfType<Ok<TokenResponse>>();
    }

    // ─── The third rejected factor spends the challenge ─────────────────────

    [Fact]
    public async Task MfaVerify_ShouldSpendChallengeWithExhaustedCode_WhenThirdFactorIsRejected()
    {
        var fixture = new ChallengeFixture();
        var token = await fixture.MintChallengeAsync();

        await fixture.VerifyAsync(token, code: ChallengeFixture.WrongTotp());
        await fixture.VerifyAsync(token, code: ChallengeFixture.WrongTotp());
        var third = await fixture.VerifyAsync(token, code: ChallengeFixture.WrongTotp());

        third.Status.Should().Be(401);
        third.Error.Should().Be(ExhaustedCode,
            because: "the request whose rejected factor spends the challenge says so with a stable code");
    }

    [Fact]
    public async Task MfaVerify_ShouldRefuseWithExpiredCode_WhenExhaustedChallengeIsReused()
    {
        var fixture = new ChallengeFixture();
        var token = await fixture.MintChallengeAsync();

        for (var i = 0; i < 3; i++)
            await fixture.VerifyAsync(token, code: ChallengeFixture.WrongTotp());
        var fourth = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());

        fourth.Status.Should().Be(400, because: "a spent challenge names no live challenge");
        fourth.Error.Should().Be(ExpiredCode);
        fourth.Result.Should().NotBeOfType<Ok<TokenResponse>>(because: "even the correct code is too late");
    }

    // ─── Unknown / used tokens carry the expired code ───────────────────────

    [Fact]
    public async Task MfaVerify_ShouldRefuseWithExpiredCode_WhenTokenIsUnknown()
    {
        var fixture = new ChallengeFixture();

        var response = await fixture.VerifyAsync("never-issued-token", code: ChallengeFixture.CorrectTotp());

        response.Status.Should().Be(400);
        response.Error.Should().Be(ExpiredCode,
            because: "the refusal is a stable machine-readable code, not prose");
    }

    [Fact]
    public async Task MfaVerify_ShouldRefuseWithExpiredCode_WhenChallengeWasAlreadyUsed()
    {
        var fixture = new ChallengeFixture();
        var token = await fixture.MintChallengeAsync();

        var signIn = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());
        signIn.Status.Should().Be(200);

        var replay = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());

        replay.Status.Should().Be(400, because: "a successful verification spends the challenge");
        replay.Error.Should().Be(ExpiredCode);
    }

    /// <summary>
    /// Red before the fix: the handler spent the challenge and answered a bare 401, which tells a client
    /// "wrong factor, the challenge is still live".
    /// </summary>
    [Fact]
    public async Task MfaVerify_ShouldRefuseWithExpiredCode_WhenChallengeUserNoLongerExists()
    {
        var fixture = new ChallengeFixture();
        var token = await fixture.MintChallengeAsync();
        await fixture.DeleteUserAsync();

        var response = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());

        response.Status.Should().Be(400, because: "the challenge names no account that can sign in, and it is spent");
        response.Error.Should().Be(ExpiredCode,
            because: "a bare 401 would tell the client the challenge is still live");
    }

    // ─── Lockout is enforced where the guesses happen ───────────────────────

    [Fact]
    public async Task MfaVerify_ShouldReturnLocked_WhenAccountLockedAfterChallengeIssued()
    {
        var fixture = new ChallengeFixture();
        var token = await fixture.MintChallengeAsync();
        await fixture.LockUserAsync(DateTimeOffset.UtcNow.AddMinutes(15));

        var locked = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());

        locked.Status.Should().Be(423,
            because: "a challenge minted before the lockout must not keep the account open after it");
        locked.Result.Should().NotBeOfType<Ok<TokenResponse>>(because: "no session for a locked account");

        var again = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());
        again.Status.Should().Be(400, because: "the refused request spends the challenge");
        again.Error.Should().Be(ExpiredCode);
    }

    /// <summary>
    /// Pins a decision (design D6): the lockout gate covers a challenge minted by the SSO callback too, so
    /// an IdP session cannot be turned into a stream of unbounded second-factor guesses. The callback itself
    /// still mints the challenge for a locked account — refusing there needs a console fragment of its own.
    /// </summary>
    [Fact]
    public async Task MfaVerify_ShouldReturnLocked_WhenSsoChallengeBelongsToLockedAccount()
    {
        var fixture = new ChallengeFixture();
        await fixture.LockUserAsync(DateTimeOffset.UtcNow.AddMinutes(15));
        var token = await fixture.MintSsoChallengeAsync();

        var locked = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());

        locked.Status.Should().Be(423, because: "the account lock applies whichever sign-in minted the challenge");
        locked.Result.Should().NotBeOfType<Ok<TokenResponse>>();
    }

    // ─── The bound holds under concurrency ──────────────────────────────────

    [Fact]
    public async Task MfaVerify_ShouldEvaluateAtMostThreeFactors_WhenWrongAttemptsRace()
    {
        const int racers = 8;
        var fixture = new ChallengeFixture();
        var token = await fixture.MintChallengeAsync();
        var wrong = ChallengeFixture.WrongTotp();

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, racers)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await fixture.VerifyAsync(token, code: wrong);
            }))
            .ToArray();
        start.Set();
        var responses = await Task.WhenAll(tasks);

        responses.Count(r => r.Status == 401).Should().Be(MfaAttemptBound,
            because: "racing requests on one challenge evaluate exactly three factors, no more and no fewer");
        responses.Count(r => r.Error == ExhaustedCode).Should().Be(1,
            because: "exactly one racer holds the final attempt");
        responses.Where(r => r.Status != 401).Should().OnlyContain(r => r.Status == 400 && r.Error == ExpiredCode,
            because: "every racer past the bound finds the challenge spent");
        fixture.FailureEventCount.Should().Be(MfaAttemptBound,
            because: "each evaluated factor is audited once, and no more than three are evaluated");
    }

    /// <summary>
    /// Guard (green before the fix): two racing correct factors never yield two sessions.
    /// </summary>
    [Fact]
    public async Task MfaVerify_ShouldIssueAtMostOneSession_WhenCorrectAttemptsRace()
    {
        const int racers = 4;
        var fixture = new ChallengeFixture();
        var token = await fixture.MintChallengeAsync();
        var correct = ChallengeFixture.CorrectTotp();

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, racers)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await fixture.VerifyAsync(token, code: correct);
            }))
            .ToArray();
        start.Set();
        var responses = await Task.WhenAll(tasks);

        responses.Count(r => r.Status == 200).Should().Be(1,
            because: "of racing correct factors on one challenge at most one is issued a session");
    }

    // ─── The account lockout bounds guesses across every outstanding challenge ─

    /// <summary>
    /// Red before the fix: every racer read the account before any failure was recorded, so each of the
    /// <c>challenges × 3</c> attempts passed the lockout check and evaluated its factor.
    /// </summary>
    [Fact]
    public async Task MfaVerify_ShouldEvaluateNoMoreFactorsThanLockoutThreshold_WhenWrongAttemptsRaceAcrossChallenges()
    {
        const int threshold = 3;
        const int challenges = 4;
        const int racers = challenges * MfaAttemptBound;
        var fixture = new ChallengeFixture(lockoutThreshold: threshold, concurrentReaders: racers);
        var tokens = new List<string>();
        for (var i = 0; i < challenges; i++)
            tokens.Add(await fixture.MintChallengeAsync());
        var wrong = ChallengeFixture.WrongTotp();

        using var start = new ManualResetEventSlim(false);
        var tasks = tokens
            .SelectMany(token => Enumerable.Range(0, MfaAttemptBound).Select(_ => token))
            .Select(token => Task.Run(async () =>
            {
                start.Wait();
                return await fixture.VerifyAsync(token, code: wrong);
            }))
            .ToArray();
        start.Set();
        var responses = await Task.WhenAll(tasks);

        fixture.FailureEventCount.Should().Be(threshold,
            because: "the lockout threshold bounds the factors evaluated for one account, however many challenges race");
        responses.Count(r => r.Status == 423).Should().Be(racers - threshold,
            because: "every racer past the threshold is refused as locked before its factor is checked");
        (await fixture.ReadUserAsync()).IsLockedOut(DateTimeOffset.UtcNow).Should().BeTrue();
    }

    /// <summary>Guard: the attempt reserved before evaluation never locks out a user who proves the factor.</summary>
    [Fact]
    public async Task MfaVerify_ShouldIssueSessionAndLeaveAccountUnlocked_WhenCorrectFactorFollowsThresholdMinusOneFailures()
    {
        const int threshold = 3;
        var fixture = new ChallengeFixture(lockoutThreshold: threshold);
        var token = await fixture.MintChallengeAsync();
        await fixture.VerifyAsync(token, code: ChallengeFixture.WrongTotp());
        await fixture.VerifyAsync(token, code: ChallengeFixture.WrongTotp());

        var signIn = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());

        signIn.Status.Should().Be(200, because: "two failures are below a threshold of three");
        var user = await fixture.ReadUserAsync();
        user.IsLockedOut(DateTimeOffset.UtcNow).Should().BeFalse(because: "a proven factor leaves no lock behind");
        user.FailedLoginAttempts.Should().Be(0, because: "a successful sign-in resets the count");
    }

    /// <summary>Guard: once a lock expires, the account still gets one attempt, as at <c>/auth/login</c>.</summary>
    [Fact]
    public async Task MfaVerify_ShouldIssueSession_WhenLockHasExpiredAndCountIsAtThreshold()
    {
        const int threshold = 3;
        var fixture = new ChallengeFixture(lockoutThreshold: threshold);
        var token = await fixture.MintChallengeAsync();
        for (var i = 0; i < threshold; i++)
            await fixture.RecordFailureAsync(threshold, DateTimeOffset.UtcNow.AddMinutes(-1));

        var signIn = await fixture.VerifyAsync(token, code: ChallengeFixture.CorrectTotp());

        signIn.Status.Should().Be(200, because: "the lock has expired, so the next attempt is evaluated");
        (await fixture.ReadUserAsync()).IsLockedOut(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    /// <summary>
    /// A factor proven on the attempt that reached the threshold locks the account for that instant. If the
    /// bookkeeping after the proof fails (a cache error, an aborted request), the reservation is still taken
    /// back: a user who proved both factors is never left locked out by an infrastructure error.
    /// </summary>
    [Fact]
    public async Task MfaVerify_ShouldLeaveAccountUnlocked_WhenBookkeepingFailsAfterProvenFactorAtThreshold()
    {
        const int threshold = 3;
        var fixture = new ChallengeFixture(lockoutThreshold: threshold);
        var first = await fixture.MintChallengeAsync();
        await fixture.VerifyAsync(first, code: ChallengeFixture.WrongTotp());
        await fixture.VerifyAsync(first, code: ChallengeFixture.WrongTotp());
        var second = await fixture.MintChallengeAsync();
        fixture.FailChallengeConsume = true;

        var act = () => fixture.VerifyAsync(second, code: ChallengeFixture.CorrectTotp());

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await fixture.ReadUserAsync()).IsLockedOut(DateTimeOffset.UtcNow).Should().BeFalse(
            because: "the attempt that reached the threshold proved the factor, so its reservation is taken back");
    }

    private const int MfaAttemptBound = 3;

    // ─── Fixture ────────────────────────────────────────────────────────────

    private sealed record VerifyResponse(IResult Result, int Status, string? Error);

    private sealed class ChallengeFixture
    {
        private const string Secret = "JBSWY3DPEHPK3PXP";

        private readonly InMemoryUserStore _store = new();
        private readonly IUserStore _userStore;
        private readonly int _concurrentReaders;
        private readonly TaskCompletionSource _allRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        private readonly ITenantAuthConfigStore _configStore = Substitute.For<ITenantAuthConfigStore>();
        private readonly IAuthEventStore _authEventStore = Substitute.For<IAuthEventStore>();
        private readonly IRefreshTokenStore _refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        private readonly List<AuthEvent> _events = [];
        private readonly InMemoryMfaPendingCache _mfaCache = new();

        /// <summary>When set, the handler's <c>TryConsumeAsync</c> throws, as a cache outage would.</summary>
        public bool FailChallengeConsume { get; set; }
        private readonly AuthEventService _authEvents;
        private readonly AccountLockoutService _lockoutService;
        private readonly JwtTokenService _jwtService;
        private readonly RefreshTokenService _refreshService;
        private readonly RecoveryCodeService _recoveryCodes = new();
        private readonly IServiceProvider _services;
        private bool _seeded;

        /// <summary>Builds the handler's collaborators over an in-memory account store.</summary>
        /// <param name="lockoutThreshold">
        /// High by default: most tests here are about the challenge, not the account lockout, so N wrong
        /// factors must never lock the account by themselves.
        /// </param>
        /// <param name="concurrentReaders">
        /// When positive, every handler read of the account waits until this many reads have happened, so
        /// all racers decide on a copy of the account read before any failure is recorded.
        /// </param>
        public ChallengeFixture(int lockoutThreshold = 100, int concurrentReaders = 0)
        {
            _concurrentReaders = concurrentReaders;
            _userStore = concurrentReaders > 0 ? BuildBarrierStore() : _store;
            var config = new TenantAuthConfig
            {
                TenantId = TenantId,
                LockoutThreshold = lockoutThreshold,
                LockoutDurationMinutes = 15,
            };
            _configStore.GetAsync(TenantId, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<TenantAuthConfig?>(config));
            _authEventStore.SaveAsync(Arg.Do<AuthEvent>(e =>
                {
                    lock (_events)
                        _events.Add(e);
                }), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            _refreshTokenStore.SaveAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            _authEvents = new AuthEventService(_authEventStore);
            _lockoutService = new AccountLockoutService(_userStore, _configStore, _authEvents);
            _refreshService = new RefreshTokenService(_refreshTokenStore);

            var keyDir = Path.Join(
                Path.GetTempPath(), "verbara-mfa-attempts-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(keyDir);
            _jwtService = new JwtTokenService(
                keyDir,
                DataProtectionProvider.Create("Verbara.Platform.MfaVerifyChallengeAttemptsTests"));

            var services = new ServiceCollection();
            services.AddLogging();
            services.ConfigureHttpJsonOptions(o =>
                o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
            _services = services.BuildServiceProvider();
        }

        public int FailureEventCount
        {
            get
            {
                lock (_events)
                {
                    return _events.Count(e =>
                        string.Equals(e.EventType, AuthEventTypes.MfaVerificationFailure, StringComparison.Ordinal));
                }
            }
        }

        public async Task<string> MintChallengeAsync()
        {
            await EnsureSeededAsync();
            return await AuthEndpoints.GenerateMfaChallengeTokenAndStoreAsync(
                UserId, TenantId, _mfaCache, CancellationToken.None);
        }

        /// <summary>Completes an SSO sign-in for the user and returns the challenge token its redirect carries.</summary>
        public async Task<string> MintSsoChallengeAsync()
        {
            var policy = Substitute.For<IMfaPolicyEvaluator>();
            var result = await OidcEndpoints.CompleteOidcLoginAsync(
                BuildHttpContext(), _jwtService, _refreshService, _authEvents, policy, _mfaCache,
                await ReadUserAsync(),
                new OidcFlowState
                {
                    TenantId = TenantId,
                    ReturnUrl = "https://app.example.com/login",
                    CodeVerifier = "test-verifier",
                    Nonce = "test-nonce",
                    ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                },
                publicBaseUrl: "https://app.example.com", ip: null, ua: null, CancellationToken.None);

            var url = result.Should().BeOfType<RedirectHttpResult>().Subject.Url;
            url.Should().Contain("#oidc_mfa_challenge");
            const string key = "challenge_token=";
            var start = url.IndexOf(key, StringComparison.Ordinal) + key.Length;
            var end = url.IndexOf('&', start);
            return Uri.UnescapeDataString(end < 0 ? url[start..] : url[start..end]);
        }

        public async Task LockUserAsync(DateTimeOffset until)
        {
            // The store's own atomic lock write (threshold 1), as a failed sign-in elsewhere would land it.
            await RecordFailureAsync(threshold: 1, lockUntil: until);
            (await ReadUserAsync()).IsLockedOut(DateTimeOffset.UtcNow)
                .Should().BeTrue(because: "the precondition is a locked account");
        }

        /// <summary>One failed sign-in recorded by the store, as a wrong password at <c>/auth/login</c> lands it.</summary>
        public async Task RecordFailureAsync(int threshold, DateTimeOffset lockUntil)
        {
            await EnsureSeededAsync();
            var recorded = await _store.RecordFailedSignInAsync(
                new TenantId(TenantId), EntityId.From(UserId), threshold, lockUntil, CancellationToken.None);
            recorded.Should().NotBeNull();
        }

        public async Task DeleteUserAsync()
        {
            await EnsureSeededAsync();
            (await _store.DeleteAsync(new TenantId(TenantId), EntityId.From(UserId), CancellationToken.None))
                .Should().BeTrue();
        }

        public async Task<User> ReadUserAsync()
        {
            var user = await _store.GetByIdAsync(new TenantId(TenantId), EntityId.From(UserId), CancellationToken.None);
            user.Should().NotBeNull();
            return user!;
        }

        /// <summary>
        /// The in-memory store behind a read barrier: the writes the handler makes go straight through.
        /// </summary>
        private IUserStore BuildBarrierStore()
        {
            var store = Substitute.For<IUserStore>();
            store.GetByIdAsync(default, default!, default).ReturnsForAnyArgs(ci =>
                ReadBehindBarrierAsync(ci.ArgAt<TenantId>(0), ci.ArgAt<EntityId>(1), ci.ArgAt<CancellationToken>(2)));
            store.RecordFailedSignInAsync(default, default!, default, default, default).ReturnsForAnyArgs(ci =>
                _store.RecordFailedSignInAsync(ci.ArgAt<TenantId>(0), ci.ArgAt<EntityId>(1), ci.ArgAt<int>(2),
                    ci.ArgAt<DateTimeOffset>(3), ci.ArgAt<CancellationToken>(4)));
            store.ResetLockoutAsync(default, default!, default, default, default).ReturnsForAnyArgs(ci =>
                _store.ResetLockoutAsync(ci.ArgAt<TenantId>(0), ci.ArgAt<EntityId>(1), ci.ArgAt<DateTimeOffset>(2),
                    ci.ArgAt<bool>(3), ci.ArgAt<CancellationToken>(4)));
            store.SetLastLoginAtAsync(default, default!, default, default).ReturnsForAnyArgs(ci =>
                _store.SetLastLoginAtAsync(ci.ArgAt<TenantId>(0), ci.ArgAt<EntityId>(1), ci.ArgAt<DateTimeOffset>(2),
                    ci.ArgAt<CancellationToken>(3)));
            return store;
        }

        private async Task<User?> ReadBehindBarrierAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
        {
            var user = await _store.GetByIdAsync(tenantId, userId, ct);
            if (Interlocked.Increment(ref _reads) >= _concurrentReaders)
                _allRead.TrySetResult();
            await _allRead.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return user;
        }

        public static string CorrectTotp() => new Totp(Base32Encoding.ToBytes(Secret)).ComputeTotp(DateTime.UtcNow);

        /// <summary>
        /// A six-digit code outside the whole verification window (±1 step, widened to ±2 for clock edges).
        /// </summary>
        public static string WrongTotp()
        {
            var totp = new Totp(Base32Encoding.ToBytes(Secret));
            var now = DateTime.UtcNow;
            var window = Enumerable.Range(-2, 5).Select(k => totp.ComputeTotp(now.AddSeconds(30.0 * k))).ToHashSet();
            var candidate = int.Parse(totp.ComputeTotp(now), System.Globalization.CultureInfo.InvariantCulture);
            string code;
            do
            {
                candidate = (candidate + 7919) % 1_000_000;
                code = candidate.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            }
            while (window.Contains(code));
            return code;
        }

        public async Task<VerifyResponse> VerifyAsync(string mfaToken, string? code = null, string? recoveryCode = null)
        {
            await EnsureSeededAsync();
            var result = await AuthEndpoints.MfaVerify(
                new MfaVerifyRequest(mfaToken, code, recoveryCode),
                BuildHttpContext(),
                _userStore,
                _jwtService,
                _refreshService,
                _lockoutService,
                _authEvents,
                FailChallengeConsume ? new ConsumeFailingCache(_mfaCache) : _mfaCache,
                _configStore,
                _recoveryCodes,
                CancellationToken.None);

            var (status, error) = await RenderAsync(result);
            return new VerifyResponse(result, status, error);
        }

        private async Task EnsureSeededAsync()
        {
            if (_seeded)
                return;
            _seeded = true;
            await _store.CreateAsync(new User
            {
                UserId = EntityId.From(UserId),
                TenantId = new TenantId(TenantId),
                Email = "mfa-attempts@test.example",
                DisplayName = "MFA Attempts User",
                Role = UserRole.Agent,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                MfaEnabled = true,
                MfaSecret = Secret,
                MfaConfirmedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        /// <summary>Executes the result as the host would and reads back the status and the <c>error</c> member.</summary>
        private async Task<(int Status, string? Error)> RenderAsync(IResult result)
        {
            var context = new DefaultHttpContext { RequestServices = _services };
            using var body = new MemoryStream();
            context.Response.Body = body;
            await result.ExecuteAsync(context);

            string? error = null;
            if (body.Length > 0
                && context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var doc = JsonDocument.Parse(body.ToArray());
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var e)
                    && e.ValueKind == JsonValueKind.String)
                {
                    error = e.GetString();
                }
            }
            return (context.Response.StatusCode, error);
        }

        private DefaultHttpContext BuildHttpContext()
        {
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", TenantId)], "TestScheme")),
                RequestServices = _services,
            };
            return context;
        }
    }

    private sealed class ConsumeFailingCache(IMfaPendingCache inner) : IMfaPendingCache
    {
        public ValueTask StoreAsync(string challengeToken, MfaPendingEntry entry, CancellationToken ct) =>
            inner.StoreAsync(challengeToken, entry, ct);

        public ValueTask<MfaChallengeAttempt?> TryBeginAttemptAsync(string? challengeToken, CancellationToken ct) =>
            inner.TryBeginAttemptAsync(challengeToken, ct);

        public ValueTask<bool> TryConsumeAsync(string? challengeToken, CancellationToken ct) =>
            throw new InvalidOperationException("simulated cache outage");
    }
}
