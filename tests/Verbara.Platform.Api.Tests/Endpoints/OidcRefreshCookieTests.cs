using System.Security.Claims;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Identity.OidcTokenExchange;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// Asserts the OIDC endpoints route the refresh-token cookie through the shared
/// <see cref="RefreshTokenCookie"/> source of truth so the Path stays in lockstep
/// with the SPA route (/api/v1/auth) on both issuance (callback) and deletion
/// (logout). A drifting Path would silently break refresh / fail to clear logout.
/// </summary>
public sealed class OidcRefreshCookieTests
{
    private const string TenantId = "oidc-cookie-test-tenant";
    private const string UserId = "oidc-cookie-user-id";
    private const string UserEmail = "oidc-cookie@test.example";

    [Fact]
    public async Task CompleteOidcLoginAsync_ShouldScopeRefreshCookieToVersionedAuthPath_WhenTokensIssued()
    {
        var fixture = new OidcCookieFixture()
            .WithUser(mfaEnabled: false)
            .WithPolicyRequiresMfa(requiresMfa: false);

        var (result, ctx) = await fixture.InvokeCompleteOidcLoginAsync();

        // Case C — happy path issues the refresh cookie + redirects.
        result.Should().BeOfType<RedirectHttpResult>();

        var setCookie = ctx.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        setCookie.Should().Contain("refresh_token=");
        setCookie.Should().Contain("path=/api/v1/auth");
        setCookie.Should().Contain("max-age=86400");
        setCookie.Should().Contain("httponly");
        setCookie.Should().Contain("samesite=strict");
    }

    [Fact]
    public async Task OidcLogout_ShouldDeleteRefreshCookieOnVersionedAuthPath_WhenCalled()
    {
        var refreshService = new RefreshTokenService(Substitute.For<IRefreshTokenStore>());
        var authEvents = new AuthEventService(Substitute.For<IAuthEventStore>());

        var ctx = BuildAuthenticatedContext();
        var result = await OidcEndpoints.OidcLogout(
            ctx, refreshService, authEvents, CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();

        var setCookie = ctx.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        setCookie.Should().Contain("refresh_token=");
        setCookie.Should().Contain("path=/api/v1/auth");
        setCookie.Should().Contain("expires=thu, 01 jan 1970");
    }

    private static DefaultHttpContext BuildAuthenticatedContext()
    {
        var ctx = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new("tid", TenantId),
            new("user_id", UserId),
        };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
        return ctx;
    }

    private sealed class OidcCookieFixture
    {
        private readonly IMfaPolicyEvaluator _mfaEvaluator = Substitute.For<IMfaPolicyEvaluator>();
        private readonly IRefreshTokenStore _refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        private readonly JwtTokenService _jwtService;
        private readonly RefreshTokenService _refreshService;
        private readonly AuthEventService _authEvents;
        private readonly InMemoryMfaPendingCache _mfaCache = new();

        private User _user = null!;

        public OidcCookieFixture()
        {
            _authEvents = new AuthEventService(Substitute.For<IAuthEventStore>());

            var tempKeyDir = Path.Combine(
                Path.GetTempPath(), "asterisk-oidc-cookie-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempKeyDir);
            _jwtService = new JwtTokenService(
                tempKeyDir,
                DataProtectionProvider.Create("Verbara.Platform.OidcCookieTests"),
                new InMemoryJtiRevocationCache());

            _refreshTokenStore.SaveAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            _refreshService = new RefreshTokenService(_refreshTokenStore);
        }

        public OidcCookieFixture WithUser(bool mfaEnabled)
        {
            _user = new User
            {
                UserId = EntityId.From(UserId),
                TenantId = new TenantId(TenantId),
                Email = UserEmail,
                DisplayName = "OIDC Cookie Test User",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                MfaEnabled = mfaEnabled,
            };
            return this;
        }

        public OidcCookieFixture WithPolicyRequiresMfa(bool requiresMfa)
        {
            _mfaEvaluator.RequiresMfaAsync(TenantId, Arg.Any<UserRole>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(requiresMfa));
            return this;
        }

        public async Task<(IResult Result, DefaultHttpContext Context)> InvokeCompleteOidcLoginAsync()
        {
            var context = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().BuildServiceProvider(),
            };
            var flowState = new OidcFlowState
            {
                TenantId = TenantId,
                ReturnUrl = "https://app.example.com/login",
                CodeVerifier = "test-verifier",
                Nonce = "test-nonce",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            };

            var result = await OidcEndpoints.CompleteOidcLoginAsync(
                context, _jwtService, _refreshService, _authEvents,
                _mfaEvaluator, _mfaCache, _user, flowState,
                ip: null, ua: null, ct: CancellationToken.None);

            return (result, context);
        }
    }
}
