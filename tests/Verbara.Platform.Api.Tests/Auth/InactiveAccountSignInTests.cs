using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Identity.OidcTokenExchange;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Every path that issues credentials refuses an account whose status is not
/// <see cref="UserStatus.Active"/>: password login, MFA completion, refresh, API-key login and the
/// OIDC callback's token issuance. Before this suite, only the OIDC callback looked at the status,
/// so a suspended or deactivated user kept signing in, refreshing and exchanging keys.
/// </summary>
/// <remarks>
/// The handlers are invoked directly (InternalsVisibleTo), the same way
/// <c>MfaPolicyEnforcementTests</c> exercises them, so each test pins the real gate.
/// </remarks>
public sealed class InactiveAccountSignInTests
{
    private const string TenantId = AuthHandlerFixture.TenantId;
    private const string UserId = AuthHandlerFixture.UserId;
    private const string Email = AuthHandlerFixture.Email;
    private const string Password = AuthHandlerFixture.Password;
    private const string RawApiKey = "inactive-account-raw-key";

    public static TheoryData<UserStatus> InactiveStatuses => new()
    {
        UserStatus.Suspended,
        UserStatus.Deactivated,
    };

    // ─── Password login ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(InactiveStatuses))]
    public async Task Login_ShouldReturn403WithoutTokens_WhenAccountIsNotActive(UserStatus status)
    {
        var fixture = NewFixture(mfaEnabled: false);
        fixture.User.Status = status;
        var context = AuthHandlerFixture.BuildHttpContext();

        var result = await InvokeLoginAsync(fixture, context, Password);

        var json = result.Should().BeOfType<JsonHttpResult<ErrorResponse>>(
            because: "a correct password must not sign in an account that is not Active").Subject;
        json.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        RefreshCookieWasSet(context).Should().BeFalse(because: "no refresh token may be handed out");
        await fixture.RefreshTokenStore.DidNotReceive().SaveAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Login_ShouldNotIssueMfaChallenge_WhenAccountIsSuspended()
    {
        var fixture = NewFixture(mfaEnabled: true);
        fixture.User.Status = UserStatus.Suspended;

        var result = await InvokeLoginAsync(fixture, AuthHandlerFixture.BuildHttpContext(), Password);

        result.Should().NotBeOfType<Ok<MfaChallengeResponse>>(
            because: "a suspended account must be refused before a second-factor challenge is minted");
        result.Should().BeOfType<JsonHttpResult<ErrorResponse>>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Login_ShouldRecordLoginFailureWithStatusReason_WhenAccountIsSuspended()
    {
        var fixture = NewFixture(mfaEnabled: false);
        fixture.User.Status = UserStatus.Suspended;

        await InvokeLoginAsync(fixture, AuthHandlerFixture.BuildHttpContext(), Password);

        await fixture.AuthEventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e.EventType == AuthEventTypes.LoginFailure
                && ReasonOf(e) == "account_suspended"),
            Arg.Any<CancellationToken>());
        fixture.User.FailedLoginAttempts.Should().Be(0,
            because: "the password was correct — the refusal must not count toward lockout");
    }

    [Fact]
    public async Task Login_ShouldReturn401AndCountTheAttempt_WhenPasswordIsWrongForSuspendedAccount()
    {
        // The status is only revealed to a caller who proved the password: a wrong password on a
        // suspended account is indistinguishable from a wrong password on an active one.
        var fixture = NewFixture(mfaEnabled: false);
        fixture.User.Status = UserStatus.Suspended;

        var result = await InvokeLoginAsync(fixture, AuthHandlerFixture.BuildHttpContext(), "WrongPassword123!");

        result.Should().BeOfType<UnauthorizedHttpResult>();
        fixture.User.FailedLoginAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Login_ShouldIssueTokens_WhenAccountIsActive()
    {
        var fixture = NewFixture(mfaEnabled: false);

        var result = await InvokeLoginAsync(fixture, AuthHandlerFixture.BuildHttpContext(), Password);

        result.Should().BeOfType<Ok<TokenResponse>>(because: "the gate must not touch an Active account");
    }

    // ─── MFA completion ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(InactiveStatuses))]
    public async Task MfaVerify_ShouldReturn403WithoutTokens_WhenAccountLeftActiveAfterTheChallenge(UserStatus status)
    {
        // The challenge was issued while the account was Active; an admin suspends it before the
        // second factor is submitted. A valid recovery code must not finish the sign-in.
        var fixture = NewFixture(mfaEnabled: true);
        var recoveryCodes = new RecoveryCodeService();
        var plaintext = recoveryCodes.Generate();
        fixture.User.MfaRecoveryCodes = plaintext.Select(c => recoveryCodes.Hash(c, UserId)).ToList();
        var challenge = await AuthEndpoints.GenerateMfaChallengeTokenAndStoreAsync(
            UserId, TenantId, fixture.MfaPendingCache, CancellationToken.None);
        fixture.User.Status = status;
        var context = AuthHandlerFixture.BuildHttpContext();

        var result = await AuthEndpoints.MfaVerify(
            new MfaVerifyRequest(challenge, Code: null, RecoveryCode: plaintext[0]),
            context,
            fixture.UserStore,
            fixture.JwtService,
            fixture.RefreshService,
            fixture.LockoutService,
            fixture.AuthEvents,
            fixture.MfaPendingCache,
            fixture.ConfigStore,
            recoveryCodes,
            CancellationToken.None);

        result.Should().BeOfType<JsonHttpResult<ErrorResponse>>(
            because: "completing MFA must not sign in an account that is no longer Active")
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        RefreshCookieWasSet(context).Should().BeFalse();
        fixture.User.MfaRecoveryCodes.Should().HaveCount(plaintext.Count,
            because: "the refusal happens before the factor is evaluated, so no recovery code is burned");
    }

    // ─── Refresh ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(InactiveStatuses))]
    public async Task Refresh_ShouldReturn401AndRevokeTheLineage_WhenAccountIsNotActive(UserStatus status)
    {
        var fixture = NewFixture(mfaEnabled: false);
        var (rawToken, _) = await fixture.RefreshService.GenerateAsync(
            UserId, TenantId, ipAddress: "10.0.0.1", userAgent: "test", CancellationToken.None);
        fixture.User.Status = status;
        var context = AuthHandlerFixture.BuildHttpContext(refreshCookie: rawToken);

        var result = await AuthEndpoints.Refresh(
            context,
            fixture.UserStore,
            fixture.JwtService,
            fixture.RefreshService,
            fixture.RefreshTokenStore,
            fixture.ConfigStore,
            fixture.MfaPolicyEvaluator,
            fixture.AuthEvents,
            CancellationToken.None);

        result.Should().BeOfType<JsonHttpResult<ErrorResponse>>(
            because: "a refresh token must stop minting access tokens once the account leaves Active")
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await fixture.RefreshTokenStore.Received(1).RevokeAllForUserAsync(
            TenantId, UserId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        string.Join(";", context.Response.Headers.SetCookie.ToArray()).Should().Contain("refresh_token=",
            because: "the dead cookie must be cleared so the browser stops replaying it");
        await fixture.AuthEventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e.EventType == AuthEventTypes.SessionRevoked
                && ReasonOf(e) == "account_" + status.ToString().ToLowerInvariant()),
            Arg.Any<CancellationToken>());
    }

    // ─── API-key login ───────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(InactiveStatuses))]
    public async Task ApiKeyLogin_ShouldReturn403WithoutToken_WhenKeyOwnerIsNotActive(UserStatus status)
    {
        var fixture = NewFixture(mfaEnabled: false).WithApiKey(ApiKeyType.Standard, RawApiKey);
        fixture.User.Status = status;

        var result = await fixture.InvokeApiKeyLoginAsync(new ApiKeyLoginRequest(RawApiKey));

        result.Should().BeOfType<JsonHttpResult<ErrorResponse>>(
            because: "a user-bound key must not mint a JWT for an owner who may not sign in")
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ApiKeyLogin_ShouldIssueToken_WhenKeyOwnerIsActive()
    {
        var fixture = NewFixture(mfaEnabled: false).WithApiKey(ApiKeyType.Standard, RawApiKey);

        var result = await fixture.InvokeApiKeyLoginAsync(new ApiKeyLoginRequest(RawApiKey));

        result.Should().BeOfType<Ok<TokenResponse>>();
    }

    // ─── OIDC token issuance ─────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(InactiveStatuses))]
    public async Task CompleteOidcLogin_ShouldReturn401WithoutTokens_WhenAccountIsNotActive(UserStatus status)
    {
        var fixture = NewFixture(mfaEnabled: false);
        fixture.User.Status = status;
        var context = AuthHandlerFixture.BuildHttpContext();

        var result = await InvokeCompleteOidcLoginAsync(fixture, context);

        result.Should().BeOfType<UnauthorizedHttpResult>(
            because: "the token-issuing OIDC step must refuse an account that is not Active");
        RefreshCookieWasSet(context).Should().BeFalse();
        await fixture.AuthEventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e.EventType == AuthEventTypes.OidcLoginFailure
                && ReasonOf(e) == "account_" + status.ToString().ToLowerInvariant()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOidcLogin_ShouldIssueTokens_WhenAccountIsActive()
    {
        var fixture = NewFixture(mfaEnabled: false);

        var result = await InvokeCompleteOidcLoginAsync(fixture, AuthHandlerFixture.BuildHttpContext());

        result.Should().BeOfType<RedirectHttpResult>()
            .Which.Url.Should().Contain("access_token=");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static AuthHandlerFixture NewFixture(bool mfaEnabled) =>
        new AuthHandlerFixture()
            .WithUser(mfaEnabled, UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig { TenantId = TenantId, MfaPolicy = "optional" });

    private static Task<IResult> InvokeLoginAsync(AuthHandlerFixture fixture, HttpContext context, string password) =>
        AuthEndpoints.Login(
            new LoginRequest(TenantId, Email, password),
            context,
            fixture.UserStore,
            fixture.LockoutService,
            fixture.JwtService,
            fixture.RefreshService,
            fixture.AuthEvents,
            fixture.ConfigStore,
            fixture.MfaPolicyEvaluator,
            fixture.MfaPendingCache,
            authWriteQueue: null,
            CancellationToken.None);

    private static Task<IResult> InvokeCompleteOidcLoginAsync(AuthHandlerFixture fixture, HttpContext context) =>
        OidcEndpoints.CompleteOidcLoginAsync(
            context,
            fixture.JwtService,
            fixture.RefreshService,
            fixture.AuthEvents,
            fixture.MfaPolicyEvaluator,
            fixture.MfaPendingCache,
            fixture.User,
            new OidcFlowState
            {
                TenantId = TenantId,
                ReturnUrl = "https://app.example.test/",
                CodeVerifier = "verifier",
                Nonce = "nonce",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            },
            publicBaseUrl: "https://app.example.test",
            ip: null,
            ua: null,
            ct: CancellationToken.None);

    private static bool RefreshCookieWasSet(HttpContext context) =>
        context.Response.Headers.SetCookie.Any(c => c is not null
            && c.StartsWith(RefreshTokenCookie.Name + "=", StringComparison.Ordinal)
            && !c.StartsWith(RefreshTokenCookie.Name + "=;", StringComparison.Ordinal));

    private static string? ReasonOf(AuthEvent e) =>
        e.Details is not null && e.Details.RootElement.TryGetProperty("reason", out var reason)
            ? reason.GetString()
            : null;
}
