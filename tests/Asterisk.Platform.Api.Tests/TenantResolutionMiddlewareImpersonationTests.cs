using System.Security.Claims;
using Asterisk.Platform.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace Asterisk.Platform.Api.Tests;

public sealed class TenantResolutionMiddlewareImpersonationTests
{
    private static DefaultHttpContext BuildContextWithImpersonation(string method, string path, bool readOnly = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Host = new HostString("localhost");

        var claims = new List<Claim>
        {
            new("impersonation", "true"),
            new("tid", "tenant1"),
            new("sub", "user1"),
        };
        if (readOnly)
            claims.Add(new Claim("readonly", "true"));

        var identity = new ClaimsIdentity(claims, "TestScheme");
        ctx.User = new ClaimsPrincipal(identity);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<int> RunMiddlewareAsync(HttpContext ctx)
    {
        var called = false;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(ctx);
        return called ? 0 : ctx.Response.StatusCode;
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_MfaSetup_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", "/api/v1/auth/mfa/setup");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_MfaConfirm_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", "/api/v1/auth/mfa/confirm");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_MfaDisable_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("DELETE", "/api/v1/auth/mfa");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_RecoveryCodesRegenerate_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", "/api/v1/auth/mfa/recovery-codes/regenerate");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_ChangePassword_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", "/api/v1/auth/change-password");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_RevokeOtherSessions_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("POST", "/api/v1/auth/sessions/revoke-others");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }

    [Fact]
    public async Task ImpersonationMiddleware_ShouldBlock_RevokeSession_WhenImpersonating()
    {
        var ctx = BuildContextWithImpersonation("DELETE", "/api/v1/auth/sessions/token-123");
        var status = await RunMiddlewareAsync(ctx);
        status.Should().Be(403);
    }
}
