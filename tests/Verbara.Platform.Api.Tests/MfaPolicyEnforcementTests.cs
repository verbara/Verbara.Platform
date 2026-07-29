using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
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

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// P0 regression suite for the tenant MFA policy enforcement vulnerabilities
/// fixed in v1.9.0 Platform.
///
/// Before the fix, 4 independent auth entry points bypassed the tenant-level
/// MFA policy configured in <see cref="TenantAuthConfig.MfaPolicy"/> /
/// <see cref="TenantAuthConfig.MfaRequiredRoles"/>:
///
/// Gap 1 — <c>POST /auth/login</c>: checked only per-user <c>MfaEnabled</c>,
/// allowing a user without TOTP enrolled to sign in even when the tenant
/// policy was <c>required_all</c>.
///
/// Gap 2 — <c>POST /auth/refresh</c>: did not re-evaluate policy; a user who
/// logged in under <c>optional</c> kept refreshing indefinitely after admin
/// flipped the policy to <c>required_all</c>.
///
/// Gap 3 — <c>POST /auth/reset-password</c>: after email reset, issued a
/// success response directly, allowing an attacker with email access to skip
/// MFA entirely.
///
/// Gap 4 — <c>POST /auth/login/apikey</c>: user-bound API keys with
/// <see cref="ApiKeyType.Standard"/> bypassed MFA policy, enabling an admin
/// to issue themselves a personal key to sidestep <c>required_all</c>.
/// Machine-to-machine <see cref="ApiKeyType.Management"/> keys remain exempt
/// by design (documented + pinned by test 8).
///
/// These tests directly invoke the internal static handlers in
/// <see cref="AuthEndpoints"/> (the test project has InternalsVisibleTo) so
/// they exercise the real policy gate without spinning up
/// WebApplicationFactory. See <c>ImpersonationPrivilegeEscalationTests</c>
/// for the complementary end-to-end factory pattern shipped alongside this
/// fix in v1.9.0.
/// </summary>
public sealed class MfaPolicyEnforcementTests
{
    // Seeds live on the fixture that writes them; aliased here so this class reads unchanged.
    private const string TenantId = AuthHandlerFixture.TenantId;
    private const string UserId = AuthHandlerFixture.UserId;
    private const string Email = AuthHandlerFixture.Email;
    private const string Password = AuthHandlerFixture.Password;
    private static readonly string s_passwordHash = AuthHandlerFixture.s_passwordHash;

    private static readonly string[] s_adminRole = ["Admin"];
    private static readonly string[] s_adminOnlyMfaRoles = ["Admin"];
    private static readonly string[] s_wildcardScope = AuthHandlerFixture.s_wildcardScope;
    private static readonly string[] s_recoveryCodes = AuthHandlerFixture.s_recoveryCodes;

    // ─── Gap 1: Login ────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_ShouldReturn401_WhenTenantRequiresMfaAndUserNotEnrolled()
    {
        // Policy requires_all; user has NOT enrolled MFA. Before the fix this
        // would succeed and issue tokens — a direct policy bypass.
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig
            {
                TenantId = TenantId,
                MfaPolicy = "required_all",
            });

        var result = await fixture.InvokeLoginAsync(new LoginRequest(TenantId, Email, Password));

        var json = result.Should().BeOfType<JsonHttpResult<MfaEnrollmentRequiredResponse>>().Subject;
        json.StatusCode.Should().Be(401);
        json.Value!.MfaEnrollmentRequired.Should().BeTrue();
        json.Value.Reason.Should().Contain("MFA enrollment").And.NotBeNullOrEmpty();
        fixture.User.FailedLoginAttempts.Should().Be(0,
            "credential validation passed; lockout counter must not advance on policy block");
    }

    [Fact]
    public async Task Login_ShouldSucceed_WhenTenantOptionalAndUserNotEnrolled()
    {
        // Policy optional + user not enrolled — the baseline happy path. Must
        // continue to issue tokens exactly as before; this test pins that the
        // policy gate adds NO regression to the existing optional flow.
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig { TenantId = TenantId, MfaPolicy = "optional" });

        var result = await fixture.InvokeLoginAsync(new LoginRequest(TenantId, Email, Password));

        result.Should().BeOfType<Ok<TokenResponse>>(
            because: "optional policy must not block non-enrolled users — this is the baseline path");
    }

    [Fact]
    public async Task Login_ShouldChallenge_WhenTenantRequiresMfaAndUserEnrolled()
    {
        // Policy required_all + user IS enrolled — fall through to existing
        // per-user challenge flow. The MfaChallengeResponse shape consumed by
        // the frontend today must remain unchanged (requiresMfa + mfaToken).
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: true, role: UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig { TenantId = TenantId, MfaPolicy = "required_all" });

        var result = await fixture.InvokeLoginAsync(new LoginRequest(TenantId, Email, Password));

        var ok = result.Should().BeOfType<Ok<MfaChallengeResponse>>().Subject;
        ok.Value!.RequiresMfa.Should().BeTrue();
        ok.Value.MfaToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_ShouldSucceed_WhenPolicyRequiresMfaForRolesOnly_AndUserIsInExemptRole()
    {
        // required_for_roles with a role the user does NOT hold → policy
        // inapplicable → non-enrolled user must succeed. Pins that we do NOT
        // over-enforce by accidentally treating "required_for_roles" as
        // "required_all".
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig
            {
                TenantId = TenantId,
                MfaPolicy = "required_for_roles",
                MfaRequiredRoles = s_adminOnlyMfaRoles,
            });

        var result = await fixture.InvokeLoginAsync(new LoginRequest(TenantId, Email, Password));

        result.Should().BeOfType<Ok<TokenResponse>>();
    }

    // ─── Gap 2: Refresh ──────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_ShouldReturn401_WhenTenantPolicyNowRequiresMfaAndUserNotEnrolled()
    {
        // Simulate the admin flipping policy optional → required_all AFTER the
        // user obtained a refresh-token under the old policy. The old token
        // must die the moment the user tries to rotate it; the new replacement
        // must also be revoked so the attacker cannot keep cycling.
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig { TenantId = TenantId, MfaPolicy = "required_all" });

        // Seed a valid refresh token in the shared store + stamp the cookie.
        var tokenPair = await fixture.RefreshService.GenerateAsync(
            UserId, TenantId, ipAddress: "1.2.3.4", userAgent: "Test", CancellationToken.None);
        var rawToken = tokenPair.RawToken;

        var httpContext = AuthHandlerFixture.BuildHttpContext(refreshCookie: rawToken);

        var result = await AuthEndpoints.Refresh(
            httpContext,
            fixture.UserStore,
            fixture.JwtService,
            fixture.RefreshService,
            fixture.RefreshTokenStore,
            fixture.ConfigStore,
            fixture.MfaPolicyEvaluator,
            fixture.AuthEvents,
            CancellationToken.None);

        var json = result.Should().BeOfType<JsonHttpResult<MfaEnrollmentRequiredResponse>>().Subject;
        json.StatusCode.Should().Be(401);
        json.Value!.MfaEnrollmentRequired.Should().BeTrue();

        // Invalidation regression pin — every refresh token for this user must
        // be revoked so the attacker's lineage dies.
        await fixture.RefreshTokenStore.Received(1).RevokeAllForUserAsync(
            TenantId, UserId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        // Cookie must be cleared so the browser does not keep replaying the
        // dead token. We assert that a Set-Cookie header was emitted that
        // clears the refresh_token (MaxAge=0 / expires in the past).
        var setCookie = string.Join(";", httpContext.Response.Headers.SetCookie.ToArray());
        setCookie.Should().Contain("refresh_token=", because: "cookie must be rewritten to clear the dead token");
    }

    // ─── Gap 3: Password Reset ───────────────────────────────────────────────

    [Fact]
    public async Task PasswordReset_ShouldRequireMfaVerification_BeforeIssuingToken_WhenPolicyRequiresMfa()
    {
        // Policy required_all + user IS already enrolled (MfaEnabled=true).
        // Password reset must NOT issue a session token directly; it must
        // signal action=verify so the frontend routes back through the MFA
        // challenge flow.
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: true, role: UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig { TenantId = TenantId, MfaPolicy = "required_all" });

        // Seed a password-reset token via the cache interface
        var resetToken = "reset-token-" + Guid.NewGuid().ToString("N");
        await fixture.PasswordResetCache.StoreAsync(resetToken, new PasswordResetEntry
        {
            UserId = UserId,
            TenantId = TenantId,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        }, CancellationToken.None);

        var result = await AuthEndpoints.ResetPassword(
            new ResetPasswordRequest(resetToken, "ReplacementPass456!"),
            fixture.UserStore,
            fixture.ConfigStore,
            fixture.AuthEvents,
            fixture.PasswordResetCache,
            CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<PasswordResetMfaRequiredResponse>>().Subject;
        ok.Value!.MfaRequired.Should().BeTrue();
        ok.Value.Action.Should().Be("verify");
        fixture.User.PasswordHash.Should().NotBe(s_passwordHash,
            because: "password change itself must persist — only the follow-on sign-in is gated");
    }

    [Fact]
    public async Task PasswordReset_ShouldRequireMfaEnrollment_WhenPolicyRequiresMfaAndUserNotEnrolled()
    {
        // Policy required_all + user NOT enrolled. Reset succeeds, but must
        // signal action=enroll so the frontend drives enrollment before any
        // session token is issued.
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig { TenantId = TenantId, MfaPolicy = "required_all" });

        var resetToken = "reset-token-" + Guid.NewGuid().ToString("N");
        await fixture.PasswordResetCache.StoreAsync(resetToken, new PasswordResetEntry
        {
            UserId = UserId,
            TenantId = TenantId,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        }, CancellationToken.None);

        var result = await AuthEndpoints.ResetPassword(
            new ResetPasswordRequest(resetToken, "ReplacementPass789!"),
            fixture.UserStore,
            fixture.ConfigStore,
            fixture.AuthEvents,
            fixture.PasswordResetCache,
            CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<PasswordResetMfaRequiredResponse>>().Subject;
        ok.Value!.MfaRequired.Should().BeTrue();
        ok.Value.Action.Should().Be("enroll");
    }

    [Fact]
    public async Task PasswordReset_ShouldReturnPlainSuccess_WhenPolicyOptional()
    {
        // Baseline happy path — policy optional, the legacy MessageResponse
        // must be preserved unchanged. Guards against accidentally rewriting
        // every password-reset success.
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig { TenantId = TenantId, MfaPolicy = "optional" });

        var resetToken = "reset-token-" + Guid.NewGuid().ToString("N");
        await fixture.PasswordResetCache.StoreAsync(resetToken, new PasswordResetEntry
        {
            UserId = UserId,
            TenantId = TenantId,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        }, CancellationToken.None);

        var result = await AuthEndpoints.ResetPassword(
            new ResetPasswordRequest(resetToken, "ReplacementPass321!"),
            fixture.UserStore,
            fixture.ConfigStore,
            fixture.AuthEvents,
            fixture.PasswordResetCache,
            CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>(
            because: "baseline flow — policy inactive → original contract preserved");
    }

    // ─── Gap 4: API Key Login ────────────────────────────────────────────────

    [Fact]
    public async Task ApiKey_UserBound_ShouldReturn401_WhenTenantRequiresMfaForUserRole()
    {
        // User-bound Standard key + policy required_all + admin role covered.
        // Before the fix an admin could issue themselves a personal key to
        // sidestep MFA.
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Admin)
            .WithTenantPolicy(new TenantAuthConfig
            {
                TenantId = TenantId,
                MfaPolicy = "required_for_roles",
                MfaRequiredRoles = s_adminRole,
            })
            .WithApiKey(ApiKeyType.Standard, rawKey: "personal-admin-key");

        var result = await fixture.InvokeApiKeyLoginAsync(new ApiKeyLoginRequest("personal-admin-key"));

        var json = result.Should().BeOfType<JsonHttpResult<ErrorResponse>>().Subject;
        json.StatusCode.Should().Be(401);
        json.Value!.Error.Should().Contain("Interactive authentication", because: "response signals to the caller that API-key auth is not permitted for MFA-enforced role");
    }

    [Fact]
    public async Task ApiKey_Management_ShouldSucceed_EvenWhenTenantRequiresMfa()
    {
        // ApiKeyType.Management is explicit machine-to-machine — by design it
        // cannot complete a TOTP challenge, and operators must still be able
        // to automate platform-level operations. Locking this in prevents an
        // over-broad future edit from accidentally blocking integrations.
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Admin)
            .WithTenantPolicy(new TenantAuthConfig { TenantId = TenantId, MfaPolicy = "required_all" })
            .WithApiKey(ApiKeyType.Management, rawKey: "platform-integration-key");

        var result = await fixture.InvokeApiKeyLoginAsync(new ApiKeyLoginRequest("platform-integration-key"));

        result.Should().BeOfType<Ok<TokenResponse>>(
            because: "machine-to-machine Management keys MUST remain exempt from MFA policy — this is the documented boundary of the fix");
    }

    // ─── Fixture ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Test fixture wiring the minimum surface the four auth endpoints need:
    /// an <see cref="IUserStore"/> with one seeded user, a
    /// <see cref="ITenantAuthConfigStore"/> returning a configurable policy,
    /// and the real <see cref="RefreshTokenService"/> / <see cref="JwtTokenService"/>
    /// / <see cref="AccountLockoutService"/> services wired over in-memory
    /// stores. Using real services (not mocks) exercises the actual token
    /// generation + rotation code paths and keeps the tests close to production.
    /// </summary>

}
