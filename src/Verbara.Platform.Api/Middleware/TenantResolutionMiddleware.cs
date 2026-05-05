using System.Security.Claims;
using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;

namespace Verbara.Platform.Api.Middleware;

internal sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly HashSet<string> BlockedImpersonationPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/management/impersonate",
        "/api/setup",
        "/api/v1/management/impersonate",
        "/api/v1/setup",
        // Security-critical auth operations (Sub C T0.2)
        "/api/v1/auth/mfa/setup",
        "/api/v1/auth/mfa/confirm",
        "/api/v1/auth/mfa/recovery-codes/regenerate",
        "/api/v1/auth/change-password",
        "/api/v1/auth/sessions/revoke-others",
        // R5.2 PA.2 — profile-scoped MFA wizard surface mirrors the security
        // gates of the legacy /auth/mfa surface.
        "/api/v1/profile/security/mfa/enroll/init",
        "/api/v1/profile/security/mfa/enroll/verify",
        "/api/v1/profile/security/mfa/enroll/complete",
        "/api/v1/profile/security/recovery-codes/regenerate",
    };

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantId = await ResolveTenantIdAsync(context);

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

        // Block write operations during read-only impersonation
        if (IsReadOnlyImpersonation(context) && IsBlockedInReadOnlyMode(context))
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            var error = new ErrorResponse("Operation not allowed in read-only impersonation mode");
            await JsonSerializer.SerializeAsync(context.Response.Body, error, ApiJsonContext.Default.ErrorResponse);
            return;
        }

        await _next(context);
    }

    // Reserved first-path segments for outbound webhook management endpoints — NOT tenant IDs.
    private static readonly HashSet<string> ReservedWebhookSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "subscriptions", "event-types", "dead-letter", "deliveries",
    };

    private static async ValueTask<TenantId?> ResolveTenantIdAsync(HttpContext context)
    {
        // Inbound channel webhooks: /api/webhooks/{tenantId}/{channel} or /api/v1/webhooks/{tenantId}/{channel}.
        // Outbound subscription management (/webhooks/subscriptions, /webhooks/event-types, ...) must NOT
        // be mistaken for a tenant path — fall back to header/subdomain/JWT for those.
        if (context.Request.Path.StartsWithSegments("/api/webhooks", out var remaining)
            || context.Request.Path.StartsWithSegments("/api/v1/webhooks", out remaining))
        {
            var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is { Length: >= 2 }
                && !string.IsNullOrWhiteSpace(segments[0])
                && !ReservedWebhookSegments.Contains(segments[0]))
                return new TenantId(segments[0]);
        }

        // Subdomain: acme.platform.com → "acme"
        var fromSubdomain = await ResolveFromSubdomainAsync(context);
        if (fromSubdomain is not null)
            return fromSubdomain;

        // X-Tenant-Id header
        if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue))
        {
            return new TenantId(headerValue.ToString());
        }

        return null;
    }

    private static async ValueTask<TenantId?> ResolveFromSubdomainAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        var dotIndex = host.IndexOf('.');
        if (dotIndex <= 0)
            return null;

        var subdomain = host[..dotIndex];
        if (subdomain is "www" or "api" or "localhost")
            return null;

        // Try branding store first (white-label subdomain mapping)
        var brandingStore = context.RequestServices.GetService<ITenantBrandingStore>();
        if (brandingStore is not null)
        {
            var branding = await brandingStore.GetBySubdomainAsync(subdomain, context.RequestAborted);
            if (branding is not null)
                return new TenantId(branding.TenantId);
        }

        // Fallback: use subdomain directly as tenantId (backward compat)
        return new TenantId(subdomain);
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

        // DELETE /api/management/tenants/* or /api/v1/management/tenants/*
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && (path.StartsWith("/api/management/tenants/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/v1/management/tenants/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // PUT /api/management/system/* or /api/v1/management/system/*
        if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
            && (path.StartsWith("/api/management/system/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/v1/management/system/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Sub C T0.2: DELETE security-critical auth paths
        // DELETE /api/v1/auth/mfa (disable MFA)
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && path.Equals("/api/v1/auth/mfa", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // DELETE /api/v1/auth/sessions/{tokenId}
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/v1/auth/sessions/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Sub C T0.2 (follow-up): DELETE /api/v1/admin/auth/sessions/{sessionId}
        // Protects the existing admin session endpoint against full impersonation abuse.
        // Read-only impersonation already blocks this via IsBlockedInReadOnlyMode.
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && (path.StartsWith("/api/v1/admin/auth/sessions/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/admin/auth/sessions/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool IsReadOnlyImpersonation(HttpContext context)
    {
        return IsImpersonating(context)
            && context.User.FindFirstValue("readonly") == "true";
    }

    internal static bool IsBlockedInReadOnlyMode(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "";

        // GET, HEAD, OPTIONS always allowed
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            return false;

        // DELETE /management/impersonate always allowed (end session)
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && (path.Equals("/api/v1/management/impersonate", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/management/impersonate", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Block all other DELETE, PUT, PATCH
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase))
            return true;

        // POST: allow safe read-only operations
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/sse", StringComparison.OrdinalIgnoreCase))
                return false;
            if (path.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return false;
            if (path.Contains("/export", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        return false;
    }
}
