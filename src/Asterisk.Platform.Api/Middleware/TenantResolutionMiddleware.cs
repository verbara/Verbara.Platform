using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Middleware;

internal sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        TenantId? tenantId = null;

        // Webhook routes: /api/webhooks/{tenantId}/{channel}
        if (context.Request.Path.StartsWithSegments("/api/webhooks", out var remaining))
        {
            var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is { Length: >= 1 } && !string.IsNullOrWhiteSpace(segments[0]))
                tenantId = new TenantId(segments[0]);
        }

        // Subdomain: acme.platform.com → "acme"
        if (tenantId is null)
        {
            var host = context.Request.Host.Host;
            var dotIndex = host.IndexOf('.');
            if (dotIndex > 0)
            {
                var subdomain = host[..dotIndex];
                if (subdomain is not ("www" or "api" or "localhost"))
                    tenantId = new TenantId(subdomain);
            }
        }

        // API routes: X-Tenant-Id header
        if (tenantId is null &&
            context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue))
        {
            tenantId = new TenantId(headerValue.ToString());
        }

        if (tenantId is not null)
            context.Items["TenantId"] = tenantId.Value;

        await _next(context);
    }
}
