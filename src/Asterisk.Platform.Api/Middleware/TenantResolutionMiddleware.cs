using System.Security.Claims;
using System.Text.Json;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Middleware;

internal sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly HashSet<string> BlockedImpersonationPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/management/impersonate",
        "/api/setup",
    };

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantId = ResolveTenantId(context);

        if (tenantId is not null)
            context.Items["TenantId"] = tenantId.Value;

        // Block dangerous operations during impersonation
        if (IsImpersonating(context) && IsBlockedDuringImpersonation(context))
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            var error = new ErrorResponse("Operation not allowed during impersonation");
            await JsonSerializer.SerializeAsync(context.Response.Body, error, ApiJsonContext.Default.ErrorResponse);
            return;
        }

        await _next(context);
    }

    private static TenantId? ResolveTenantId(HttpContext context)
    {
        // Webhook routes: /api/webhooks/{tenantId}/{channel}
        if (context.Request.Path.StartsWithSegments("/api/webhooks", out var remaining))
        {
            var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is { Length: >= 1 } && !string.IsNullOrWhiteSpace(segments[0]))
                return new TenantId(segments[0]);
        }

        // Subdomain: acme.platform.com → "acme"
        var host = context.Request.Host.Host;
        var dotIndex = host.IndexOf('.');
        if (dotIndex > 0)
        {
            var subdomain = host[..dotIndex];
            if (subdomain is not ("www" or "api" or "localhost"))
                return new TenantId(subdomain);
        }

        // API routes: X-Tenant-Id header
        if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue))
        {
            return new TenantId(headerValue.ToString());
        }

        return null;
    }

    private static bool IsImpersonating(HttpContext context)
    {
        return context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirstValue("impersonation") == "true";
    }

    private static bool IsBlockedDuringImpersonation(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;

        // POST /api/management/impersonate (recursive impersonation)
        // POST /api/setup
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && BlockedImpersonationPaths.Contains(path))
        {
            return true;
        }

        // DELETE /api/management/tenants/*
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/management/tenants/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // PUT /api/management/system/*
        if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/management/system/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
