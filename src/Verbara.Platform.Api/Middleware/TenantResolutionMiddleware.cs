using System.Net;
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
        var (tenantId, source) = await ResolveTenantIdAsync(context);

        if (tenantId is not null)
        {
            context.Items["TenantId"] = tenantId.Value;
            TenantSourceItems.Set(context, source);
        }

        await _next(context);
    }

    // Reserved first-path segments for outbound webhook management endpoints — NOT tenant IDs.
    private static readonly HashSet<string> ReservedWebhookSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "subscriptions", "event-types", "dead-letter", "deliveries",
    };

    // First host labels that are never tenant ids (ADR-0038, case-insensitive).
    private static readonly HashSet<string> ReservedSubdomainLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "www", "api", "localhost",
    };

    // `source` is meaningful only when `tenantId` is non-null.
    private static async ValueTask<(TenantId? TenantId, TenantSource Source)> ResolveTenantIdAsync(HttpContext context)
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
                return (new TenantId(segments[0]), TenantSource.WebhookPath);
        }

        // Precedence (ADR-0038): subdomain wins over the X-Tenant-Id header. A white-label subdomain is the
        // operator's deliberate statement of which tenant this host serves; inverting the order would change
        // live behaviour for every white-label deployment. IP-literal hosts never yield a subdomain, so there
        // the header applies. JWT `tid` / API key are applied later, by the auth handlers.
        var fromSubdomain = await ResolveFromSubdomainAsync(context);
        if (fromSubdomain is not null)
            return (fromSubdomain, TenantSource.Subdomain);

        if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue))
        {
            return (new TenantId(headerValue.ToString()), TenantSource.Header);
        }

        return (null, default);
    }

    private static async ValueTask<TenantId?> ResolveFromSubdomainAsync(HttpContext context)
    {
        // Trailing dot (FQDN form) is trimmed so "127.0.0.1." cannot slip past the IP guard as tenant "127".
        var host = context.Request.Host.Host.TrimEnd('.');

        // IP-literal hosts (incl. bracketed IPv6 — HostString keeps the brackets, TryParse accepts them)
        // carry no subdomain: "192.168.1.50" must never resolve tenant "192".
        if (IPAddress.TryParse(host, out _))
            return null;

        var dotIndex = host.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex <= 0)
            return null;

        var subdomain = host[..dotIndex];
        if (ReservedSubdomainLabels.Contains(subdomain))
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
