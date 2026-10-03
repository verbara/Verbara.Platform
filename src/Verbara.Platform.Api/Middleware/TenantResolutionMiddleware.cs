using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;

namespace Verbara.Platform.Api.Middleware;

internal sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantId = await ResolveTenantIdAsync(context);

        if (tenantId is not null)
            context.Items["TenantId"] = tenantId.Value;

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
}
