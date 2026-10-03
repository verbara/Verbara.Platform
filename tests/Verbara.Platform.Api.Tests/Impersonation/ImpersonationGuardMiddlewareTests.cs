using System.Security.Claims;
using System.Text;
using Verbara.Platform.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Verbara.Platform.Api.Tests.Impersonation;

/// <summary>
/// <see cref="ImpersonationGuardMiddleware"/> on its own, with the principal and the routed endpoint set
/// by hand. Where it runs relative to authentication is what <see cref="ImpersonationGuardPipelineTests"/>
/// covers, over the real pipeline.
/// </summary>
public sealed class ImpersonationGuardMiddlewareTests
{
    private const string Api = "/api/v{version:apiVersion}";

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_MfaSetup_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", Api + "/auth/mfa/setup");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_MfaConfirm_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", Api + "/auth/mfa/confirm");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_MfaDisable_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("DELETE", Api + "/auth/mfa");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_RecoveryCodesRegenerate_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", Api + "/auth/mfa/recovery-codes/regenerate");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_ChangePassword_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", Api + "/auth/change-password");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_RevokeOtherSessions_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", Api + "/auth/sessions/revoke-others");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_RevokeSession_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("DELETE", Api + "/auth/sessions/{tokenId}");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_ProfileSessionRevoke_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", Api + "/profile/security/sessions/{tokenId}/revoke");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldAllow_MfaSetup_WhenNotImpersonating()
    {
        // POST /mfa/setup by a regular authenticated user (no impersonation claim) must pass through
        var ctx = BuildContextWithoutImpersonation("POST", Api + "/auth/mfa/setup");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(0); // next was called (helper returns 0 when passed through)
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldAllow_MfaVerify_WhenImpersonating()
    {
        // /mfa/verify is a real route called during login BEFORE impersonation claim is on the JWT,
        // so no block is needed. Pin that it's not accidentally blocked.
        var ctx = BuildContextWithImpersonation("POST", Api + "/auth/mfa/verify");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(0);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldAllow_AdminSessionsRevoke_WhenNotImpersonating()
    {
        // A regular tenant admin revoking a session normally must NOT be blocked
        var ctx = BuildContextWithoutImpersonation("DELETE", Api + "/admin/auth/sessions/{id}");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(0);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_AdminSessionsRevoke_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("DELETE", Api + "/admin/auth/sessions/{id}");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldNotGuard_WhenPrincipalIsAnonymous()
    {
        // Claims an unauthenticated identity carries are not a token: before authentication the
        // principal is anonymous, and nothing it claims makes it an impersonation.
        var ctx = BuildContextWithImpersonation("DELETE", Api + "/admin/auth/sessions/{id}", readOnly: true);
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(ctx.User.Claims));
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(0);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldRefuseWithReason_WhenOperationIsBlocked()
    {
        var ctx = BuildContextWithImpersonation("POST", Api + "/auth/change-password");

        var status = await RunMiddlewareAsync(ctx);

        status.Should().Be(403);
        ctx.Response.ContentType.Should().Be("application/json");
        ResponseBody(ctx).Should().Contain(ImpersonationGuardMiddleware.DuringImpersonationMessage);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlockWrite_WhenReadOnly()
    {
        var ctx = BuildContextWithImpersonation("POST", Api + "/contacts/", readOnly: true);

        var status = await RunMiddlewareAsync(ctx);

        status.Should().Be(403);
        ResponseBody(ctx).Should().Contain(ImpersonationGuardMiddleware.ReadOnlyMessage);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldAllowWrite_WhenSessionIsFull()
    {
        var ctx = BuildContextWithImpersonation("POST", Api + "/contacts/", readOnly: false);
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(0);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task ImpersonationMiddleware_ShouldAllowRead_WhenReadOnly(string method)
    {
        var ctx = BuildContextWithImpersonation(method, Api + "/contacts/", readOnly: true);
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(0);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldAllowEndingTheSession_WhenReadOnly()
    {
        var ctx = BuildContextWithImpersonation("DELETE", Api + "/management/impersonate", readOnly: true);
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(0);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlockWrite_WhenReadOnlyRequestMatchedNoEndpoint()
    {
        var ctx = BuildContextWithImpersonation("POST", template: null, readOnly: true);
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static DefaultHttpContext BuildContextWithImpersonation(string method, string? template, bool readOnly = false)
    {
        var claims = new List<Claim>
        {
            new("impersonation", "true"),
            new("tid", "tenant1"),
            new("sub", "user1"),
        };
        if (readOnly)
            claims.Add(new Claim("readonly", "true"));

        return BuildContext(method, template, claims);
    }

    private static DefaultHttpContext BuildContextWithoutImpersonation(string method, string template) =>
        BuildContext(method, template, [new("tid", "tenant1"), new("sub", "user1")]);

    // The guard reads the endpoint routing selected, so each context carries one with the real
    // route template (or none, for a request that matched no endpoint).
    private static DefaultHttpContext BuildContext(string method, string? template, IEnumerable<Claim> claims)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Host = new HostString("localhost");
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
        ctx.Response.Body = new MemoryStream();
        if (template is not null)
        {
            ctx.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, EndpointMetadataCollection.Empty, template));
        }

        return ctx;
    }

    private static async Task<int> RunMiddlewareAsync(HttpContext ctx)
    {
        var called = false;
        var middleware = new ImpersonationGuardMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(ctx);
        return called ? 0 : ctx.Response.StatusCode;
    }

    private static string ResponseBody(HttpContext ctx) =>
        Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
}
