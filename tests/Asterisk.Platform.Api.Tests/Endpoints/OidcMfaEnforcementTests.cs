using Asterisk.Platform.Api.Auth;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Identity.Mfa;
using Asterisk.Platform.Identity.OidcTokenExchange;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Endpoints;

/// <summary>
/// Regression suite for the P0 security gap fixed in v1.9.2 Frente C:
/// OIDC SSO callback bypassing tenant MFA policy enforcement.
///
/// Before the fix, <c>CompleteOidcLoginAsync</c> issued access + refresh tokens
/// regardless of whether the tenant required MFA for the user's role.
/// </summary>
public sealed class OidcMfaEnforcementTests
{
    private const string TenantId = "oidc-mfa-test-tenant";
    private const string UserId = "oidc-mfa-user-id";
    private const string UserEmail = "oidc-user@test.example";
    private const string ReturnUrl = "https://app.example.com/login";

    // ─── Test 4: policy requires MFA + user not enrolled → enrollment fragment ──

    [Fact]
    public async Task OidcCallback_ShouldRedirectWithEnrollmentFragment_WhenPolicyRequiresMfaAndUserNotEnrolled()
    {
        // Arrange
        var fixture = new OidcMfaFixture()
            .WithUser(mfaEnabled: false)
            .WithPolicyRequiresMfa(requiresMfa: true);

        // Act
        var result = await fixture.InvokeCompleteOidcLoginAsync(ReturnUrl);

        // Assert — must redirect to enrollment-required fragment, NOT issue tokens.
        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Contain("#oidc_mfa_enrollment_required",
            because: "policy requires MFA but user is not enrolled — frontend must drive enrollment");
        redirect.Url.Should().Contain($"tenant_id={Uri.EscapeDataString(TenantId)}");
        redirect.Url.Should().Contain($"email={Uri.EscapeDataString(UserEmail)}");
        redirect.Url.Should().StartWith(ReturnUrl);
    }

    // ─── Test 5: user has MFA enrolled → challenge fragment + pending entry ────

    [Fact]
    public async Task OidcCallback_ShouldRedirectWithChallengeFragment_WhenUserMfaEnrolled()
    {
        // Arrange — policy true + user enrolled. Even when policy doesn't require MFA,
        // an enrolled user must complete the TOTP challenge (same as password login).
        var fixture = new OidcMfaFixture()
            .WithUser(mfaEnabled: true)
            .WithPolicyRequiresMfa(requiresMfa: true);

        // Act
        var result = await fixture.InvokeCompleteOidcLoginAsync(ReturnUrl);

        // Assert — must redirect to challenge fragment.
        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Contain("#oidc_mfa_challenge",
            because: "enrolled user must complete TOTP before receiving tokens");
        redirect.Url.Should().Contain("challenge_token=",
            because: "a pending entry must be created and the token embedded in the redirect");
        redirect.Url.Should().Contain($"tenant_id={Uri.EscapeDataString(TenantId)}");
        redirect.Url.Should().StartWith(ReturnUrl);

        // Verify the pending cache contains an entry for this challenge token.
        var fragmentPart = redirect.Url[(redirect.Url.IndexOf('#') + 1)..];
        var challengeToken = ParseQueryParam(fragmentPart, "challenge_token");
        challengeToken.Should().NotBeNullOrEmpty(because: "challenge token must be present in the redirect URL");
        AuthEndpoints.MfaPendingCache.Should().ContainKey(challengeToken!,
            because: "MfaPendingCache must hold the entry so /auth/mfa/verify can validate it");
        AuthEndpoints.MfaPendingCache[challengeToken!].TenantId.Should().Be(TenantId);
    }

    // ─── Test 6: no MFA required + user not enrolled → tokens issued normally ──

    [Fact]
    public async Task OidcCallback_ShouldIssueTokensNormally_WhenNoMfaRequired()
    {
        // Arrange — policy does NOT require MFA + user not enrolled. This is the
        // pre-existing happy path that must remain unaffected by the new gate.
        var fixture = new OidcMfaFixture()
            .WithUser(mfaEnabled: false)
            .WithPolicyRequiresMfa(requiresMfa: false);

        // Act
        var result = await fixture.InvokeCompleteOidcLoginAsync(ReturnUrl);

        // Assert — redirect to existing oidc_callback fragment with access token.
        var redirect = result.Should().BeOfType<RedirectHttpResult>().Subject;
        redirect.Url.Should().Contain("#oidc_callback",
            because: "baseline flow — no MFA gating, tokens issued directly");
        redirect.Url.Should().Contain("access_token=",
            because: "an access token must be embedded in the callback fragment");
        redirect.Url.Should().Contain($"tenant_id={Uri.EscapeDataString(TenantId)}");
        redirect.Url.Should().StartWith(ReturnUrl);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static string? ParseQueryParam(string queryString, string key)
    {
        foreach (var part in queryString.Split('&'))
        {
            var idx = part.IndexOf('=');
            if (idx < 0) continue;
            var k = part[..idx];
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(idx + 1)..]);
        }
        return null;
    }

    // ─── Fixture ─────────────────────────────────────────────────────────────────

    private sealed class OidcMfaFixture
    {
        private readonly IMfaPolicyEvaluator _mfaEvaluator = Substitute.For<IMfaPolicyEvaluator>();
        private readonly IAuthEventStore _authEventStore = Substitute.For<IAuthEventStore>();
        private readonly IRefreshTokenStore _refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        private readonly JwtTokenService _jwtService;
        private readonly RefreshTokenService _refreshService;
        private readonly AuthEventService _authEvents;
        private User _user = null!;

        public OidcMfaFixture()
        {
            _authEvents = new AuthEventService(_authEventStore);

            var tempKeyDir = Path.Combine(
                Path.GetTempPath(), "asterisk-oidc-mfa-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempKeyDir);
            _jwtService = new JwtTokenService(
                tempKeyDir,
                DataProtectionProvider.Create("Asterisk.Platform.OidcMfaTests"),
                new InMemoryJtiRevocationCache());

            // Wire a simple in-memory refresh token store.
            var tokens = new System.Collections.Concurrent.ConcurrentDictionary<string, RefreshToken>();
            _refreshTokenStore.SaveAsync(
                    Arg.Do<RefreshToken>(t => tokens[t.TokenHash] = t),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            _refreshService = new RefreshTokenService(_refreshTokenStore);
        }

        public OidcMfaFixture WithUser(bool mfaEnabled)
        {
            _user = new User
            {
                UserId = EntityId.From(UserId),
                TenantId = new TenantId(TenantId),
                Email = UserEmail,
                DisplayName = "OIDC Test User",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                MfaEnabled = mfaEnabled,
                MfaSecret = mfaEnabled ? "JBSWY3DPEHPK3PXP" : null,
                MfaConfirmedAt = mfaEnabled ? DateTimeOffset.UtcNow : null,
            };
            return this;
        }

        public OidcMfaFixture WithPolicyRequiresMfa(bool requiresMfa)
        {
            _mfaEvaluator.RequiresMfaAsync(TenantId, Arg.Any<UserRole>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(requiresMfa));
            return this;
        }

        public Task<IResult> InvokeCompleteOidcLoginAsync(string returnUrl)
        {
            var context = BuildHttpContext();
            var flowState = new OidcFlowState
            {
                TenantId = TenantId,
                ReturnUrl = returnUrl,
                CodeVerifier = "test-verifier",
                Nonce = "test-nonce",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            };

            return OidcEndpoints.CompleteOidcLoginAsync(
                context,
                _jwtService,
                _refreshService,
                _authEvents,
                _mfaEvaluator,
                _user,
                flowState,
                ip: null,
                ua: null,
                ct: CancellationToken.None);
        }

        private static DefaultHttpContext BuildHttpContext()
        {
            var context = new DefaultHttpContext();
            var services = new ServiceCollection();
            context.RequestServices = services.BuildServiceProvider();
            return context;
        }
    }
}
