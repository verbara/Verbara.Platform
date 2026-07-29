using System.Security.Claims;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Identity.Mfa;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// Failure bookkeeping on <c>POST /auth/mfa/verify</c>
/// (openspec <c>fix-recovery-code-redemption</c>, spec requirement
/// "Every failed redemption attempt is audited and counted toward lockout").
/// </summary>
/// <remarks>
/// Before this change the handler recorded NOTHING on any failure path — no auth
/// event, no lockout attempt — even though <c>AuthEndpoints.Login</c> has recorded
/// exactly that on a bad password since it shipped. Second-factor guessing against
/// the endpoint was therefore unaudited and unthrottled beyond the global
/// rate-limit bucket, which is the <c>docs/security/audit-checklist.md</c> Scope 3.4
/// gap these tests close.
/// </remarks>
public sealed class MfaVerifyBookkeepingTests
{
    private const string TenantId = "mfa-verify-tenant";
    private const string UserId = "mfa-verify-user";
    private const string RecoverySalt = UserId;

    // ─── A failed redemption is audited AND counted ─────────────────────────

    [Fact]
    public async Task MfaVerify_ShouldLogAuthEventAndRecordLockoutAttempt_WhenRecoveryCodeIsInvalid()
    {
        var fixture = new MfaVerifyFixture().WithSha256RecoveryCodes();

        var result = await fixture.InvokeAsync(recoveryCode: "NOTMINTD");

        result.Should().BeOfType<UnauthorizedHttpResult>();

        fixture.EventsOfType(AuthEventTypes.MfaVerificationFailure).Should().ContainSingle(
            because: "a rejected second factor must leave an audit trail — Scope 3.4 requires MFA "
                   + "verification to be audited, and threat-model asset A7 rests on recovery being observable");

        fixture.User.FailedLoginAttempts.Should().Be(1,
            because: "a rejected second factor must count toward the tenant lockout policy, exactly as a "
                   + "rejected password does — otherwise recovery codes can be brute-forced past the password gate");
    }

    [Fact]
    public async Task MfaVerify_ShouldNotLogTheSubmittedCode_WhenVerificationFails()
    {
        const string submitted = "SUPERSECRETCODE";
        var fixture = new MfaVerifyFixture().WithSha256RecoveryCodes();

        await fixture.InvokeAsync(recoveryCode: submitted);

        var evt = fixture.EventsOfType(AuthEventTypes.MfaVerificationFailure).Should().ContainSingle().Subject;
        var details = evt.Details?.RootElement.GetRawText() ?? "";

        details.Should().NotContain(submitted,
            because: "the submitted recovery code is a credential and must never be written to the audit log");
        details.Should().NotContain(fixture.MfaSecret,
            because: "the TOTP secret must never be written to the audit log");
        foreach (var storedDigest in fixture.User.MfaRecoveryCodes!)
        {
            details.Should().NotContain(storedDigest,
                because: "the stored digest must never be written to the audit log");
        }
    }

    // ─── The audit trail names WHICH factor failed ──────────────────────────

    [Fact]
    public async Task MfaVerify_ShouldRecordInvalidRecoveryCodeReason_WhenRecoveryCodeIsRejected()
    {
        var fixture = new MfaVerifyFixture().WithSha256RecoveryCodes();

        await fixture.InvokeAsync(recoveryCode: "NOTMINTD");

        fixture.SingleFailureReason().Should().Be("invalid_recovery_code");
    }

    [Fact]
    public async Task MfaVerify_ShouldRecordInvalidTotpReason_WhenOnlyTotpIsSupplied()
    {
        var fixture = new MfaVerifyFixture().WithSha256RecoveryCodes();

        await fixture.InvokeAsync(code: "000000");

        fixture.SingleFailureReason().Should().Be("invalid_totp",
            because: "the audit trail must distinguish a failed authenticator code from a failed recovery code");
    }

    [Fact]
    public async Task MfaVerify_ShouldRecordNoFactorSuppliedReason_WhenNeitherFactorIsSubmitted()
    {
        var fixture = new MfaVerifyFixture().WithSha256RecoveryCodes();

        await fixture.InvokeAsync();

        fixture.SingleFailureReason().Should().Be("no_factor_supplied");
    }

    // ─── Repeated failures lock the account per tenant policy ───────────────

    [Fact]
    public async Task MfaVerify_ShouldLockAccount_WhenFailedAttemptsReachTenantThreshold()
    {
        const int threshold = 3;
        var fixture = new MfaVerifyFixture()
            .WithSha256RecoveryCodes()
            .WithLockoutThreshold(threshold);

        for (var attempt = 0; attempt < threshold; attempt++)
        {
            var result = await fixture.InvokeAsync(recoveryCode: "NOTMINTD");
            result.Should().BeOfType<UnauthorizedHttpResult>();
        }

        fixture.User.FailedLoginAttempts.Should().Be(threshold);
        fixture.User.LockedUntil.Should().NotBeNull(
            because: "N failed MFA verifications must lock the account exactly as N failed password attempts would");
        fixture.User.LockedUntil.Should().BeAfter(DateTimeOffset.UtcNow,
            because: "the lock must still be in force immediately after it is applied");
        fixture.EventsOfType(AuthEventTypes.Lockout).Should().ContainSingle(
            because: "crossing the threshold emits its own lockout event on top of the per-attempt failure events");
    }

    // ─── A successful redemption resets the counter ─────────────────────────

    [Fact]
    public async Task MfaVerify_ShouldResetFailedAttemptCounter_WhenRecoveryCodeIsRedeemed()
    {
        var fixture = new MfaVerifyFixture().WithSha256RecoveryCodes();

        // Two misses, then the real thing.
        await fixture.InvokeAsync(recoveryCode: "NOTMINTD");
        await fixture.InvokeAsync(recoveryCode: "ALSOWRNG");
        fixture.User.FailedLoginAttempts.Should().Be(2);

        var result = await fixture.InvokeAsync(recoveryCode: fixture.PlaintextCodes[0]);

        result.Should().BeOfType<Ok<TokenResponse>>(
            because: "a valid recovery code completes the challenge and issues tokens");
        fixture.User.FailedLoginAttempts.Should().Be(0,
            because: "IssueTokensAsync resets the lockout counter — the success path must not leave stale failures behind");
        fixture.User.LockedUntil.Should().BeNull();
        fixture.EventsOfType(AuthEventTypes.MfaVerificationFailure).Should().HaveCount(2,
            because: "the success path adds no failure event of its own");
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    private sealed class MfaVerifyFixture
    {
        private readonly IUserStore _userStore = Substitute.For<IUserStore>();
        private readonly ITenantAuthConfigStore _configStore = Substitute.For<ITenantAuthConfigStore>();
        private readonly IAuthEventStore _authEventStore = Substitute.For<IAuthEventStore>();
        private readonly IRefreshTokenStore _refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        private readonly List<AuthEvent> _events = [];

        private readonly InMemoryMfaPendingCache _mfaCache = new();
        private readonly AuthEventService _authEvents;
        private readonly AccountLockoutService _lockoutService;
        private readonly JwtTokenService _jwtService;
        private readonly RefreshTokenService _refreshService;
        private TenantAuthConfig _config = new() { TenantId = TenantId };

        /// <summary>The real service — the salted-SHA-256 branch is only meaningful against real digests.</summary>
        public readonly RecoveryCodeService RecoveryCodes = new();

        public User User { get; }

        public string MfaSecret => User.MfaSecret!;

        public IReadOnlyList<string> PlaintextCodes { get; private set; } = [];

        public MfaVerifyFixture()
        {
            User = new User
            {
                UserId = EntityId.From(UserId),
                TenantId = new TenantId(TenantId),
                Email = "mfa-verify@test.example",
                DisplayName = "MFA Verify User",
                Role = UserRole.Agent,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                MfaEnabled = true,
                MfaSecret = "JBSWY3DPEHPK3PXP",
                MfaConfirmedAt = DateTimeOffset.UtcNow,
            };

            _userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<User?>(User));
            _userStore.SaveAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            _configStore.GetAsync(TenantId, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<TenantAuthConfig?>(_config));

            // Capture rather than Arg.Is(...) so assertions read the real payload
            // instead of silently matching nothing when a predicate is wrong.
            _authEventStore.SaveAsync(Arg.Do<AuthEvent>(e => _events.Add(e)), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            _refreshTokenStore.SaveAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            _authEvents = new AuthEventService(_authEventStore);
            _lockoutService = new AccountLockoutService(_userStore, _configStore, _authEvents);
            _refreshService = new RefreshTokenService(_refreshTokenStore);

            var keyDir = Path.Combine(
                Path.GetTempPath(), "verbara-mfa-verify-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(keyDir);
            _jwtService = new JwtTokenService(
                keyDir,
                DataProtectionProvider.Create("Verbara.Platform.MfaVerifyBookkeepingTests"),
                new InMemoryJtiRevocationCache());
        }

        /// <summary>
        /// Seeds the wizard's digest family — salted SHA-256 over
        /// <c>user.UserId.Value</c> — via the real <see cref="RecoveryCodeService"/>.
        /// </summary>
        public MfaVerifyFixture WithSha256RecoveryCodes()
        {
            PlaintextCodes = RecoveryCodes.Generate();
            User.MfaRecoveryCodes = PlaintextCodes.Select(c => RecoveryCodes.Hash(c, RecoverySalt)).ToList();
            return this;
        }

        public MfaVerifyFixture WithLockoutThreshold(int threshold)
        {
            _config = new TenantAuthConfig
            {
                TenantId = TenantId,
                LockoutThreshold = threshold,
                LockoutDurationMinutes = 15,
            };
            return this;
        }

        /// <summary>
        /// Mints a fresh challenge token (the cache consumes it on read) and invokes
        /// the real <c>MfaVerify</c> handler.
        /// </summary>
        public async Task<IResult> InvokeAsync(string? code = null, string? recoveryCode = null)
        {
            var mfaToken = await AuthEndpoints.GenerateMfaChallengeTokenAndStoreAsync(
                UserId, TenantId, _mfaCache, CancellationToken.None);

            return await AuthEndpoints.MfaVerify(
                new MfaVerifyRequest(mfaToken, code, recoveryCode),
                BuildHttpContext(),
                _userStore,
                _jwtService,
                _refreshService,
                _lockoutService,
                _authEvents,
                _mfaCache,
                _configStore,
                RecoveryCodes,
                CancellationToken.None);
        }

        public List<AuthEvent> EventsOfType(string eventType) =>
            _events.Where(e => string.Equals(e.EventType, eventType, StringComparison.Ordinal)).ToList();

        public string? SingleFailureReason()
        {
            var evt = EventsOfType(AuthEventTypes.MfaVerificationFailure).Should().ContainSingle().Subject;
            evt.Details.Should().NotBeNull(because: "the failure event must name which factor was rejected");
            return evt.Details!.RootElement.GetProperty("reason").GetString();
        }

        private static DefaultHttpContext BuildHttpContext()
        {
            var context = new DefaultHttpContext();
            var claims = new List<Claim>
            {
                new("tid", TenantId),
                new("sub", UserId),
            };
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
            context.RequestServices = new ServiceCollection().BuildServiceProvider();
            return context;
        }
    }
}
