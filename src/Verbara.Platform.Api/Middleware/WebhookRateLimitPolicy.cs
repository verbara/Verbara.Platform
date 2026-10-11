namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Rate limit for the anonymous provider-webhook route (<c>/webhooks/{tenantId}/{channel}</c>, the
/// verification <c>GET</c> and the delivery <c>POST</c>), applied through the shared anonymous
/// endpoint-filter limiter (<see cref="AnonymousRateLimitPolicy"/>).
/// <para>
/// The partition is the tenant named in the path, taken raw, together with the client address (IPv6
/// by /64). Nothing is looked up before the limit: the lookup is the per-request database work the
/// limit exists to bound, and because the client is part of the key a forged path tenant only spends
/// the forger's own bucket. A request the route answers <c>404</c> (no active channel for that tenant)
/// is additionally charged to one per-client bucket shared by every unknown tenant, so enumerating
/// tenant ids spends one small budget and never a real tenant's.
/// </para>
/// <para>
/// Limits (fixed one-minute windows) come from <c>Webhooks:RateLimit:PerMinutePerClient</c> (default
/// <see cref="DefaultPerMinutePerClient"/>, high, because a provider such as Meta delivers for many
/// tenants from few addresses) and <c>Webhooks:RateLimit:UnknownTenantPerMinutePerClient</c> (default
/// <see cref="DefaultUnknownTenantPerMinutePerClient"/>). The first 429 per tenant is logged.
/// </para>
/// </summary>
internal static class WebhookRateLimitPolicy
{
    internal const string Policy = "webhooks";
    internal const string PerMinutePerClientKey = "Webhooks:RateLimit:PerMinutePerClient";
    internal const string UnknownTenantPerMinutePerClientKey = "Webhooks:RateLimit:UnknownTenantPerMinutePerClient";
    internal const int DefaultPerMinutePerClient = 1200;
    internal const int DefaultUnknownTenantPerMinutePerClient = 60;

    /// <summary>The route value that carries the tenant.</summary>
    internal const string TenantRouteValue = "tenantId";

    private static readonly AnonymousRateLimit Limit =
        new(Policy, PerMinutePerClientKey, DefaultPerMinutePerClient, "Webhook")
        {
            UnknownTenantLimitKey = UnknownTenantPerMinutePerClientKey,
            UnknownTenantDefaultLimit = DefaultUnknownTenantPerMinutePerClient,
        };

    /// <summary>Limits every webhook endpoint the builder covers per raw route tenant and client.</summary>
    internal static TBuilder RequireWebhookRateLimit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAnonymousRateLimit(Limit, static invocation =>
            ValueTask.FromResult(invocation.HttpContext.GetRouteValue(TenantRouteValue) as string));
}
