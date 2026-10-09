using System.Net;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Rate limits for the anonymous WebChat REST endpoints (session create, message send), applied
/// through the shared anonymous endpoint-filter limiter (<see cref="AnonymousRateLimitPolicy"/>).
/// <para>
/// Each limit is partitioned per tenant and per client address. The tenant is the one the request
/// proves: for a session create, the requested tenant when it exists; for a message, the tenant of
/// the session it names. A request for an unknown tenant or session lands in one shared "unknown"
/// partition per client address. Because the session-create tenant is only in the body, the limit
/// runs as an endpoint filter after binding.
/// </para>
/// <para>
/// Limits come from configuration (fixed one-minute windows):
/// <c>WebChat:RateLimit:SessionsPerMinutePerIp</c> (default <see cref="DefaultSessionsPerMinutePerIp"/>)
/// and <c>WebChat:RateLimit:MessagesPerMinutePerIp</c> (default <see cref="DefaultMessagesPerMinutePerIp"/>).
/// A rejected request gets the shared 429 response with <c>Retry-After</c>
/// (<see cref="TenantRateLimitPolicy.WriteTooManyRequestsAsync"/>).
/// </para>
/// </summary>
internal static class WebChatRateLimitPolicy
{
    internal const string SessionsPolicy = "webchat-sessions";
    internal const string MessagesPolicy = "webchat-messages";

    internal const string SessionsLimitKey = "WebChat:RateLimit:SessionsPerMinutePerIp";
    internal const string MessagesLimitKey = "WebChat:RateLimit:MessagesPerMinutePerIp";
    internal const string TrustedProxiesKey = AnonymousRateLimitPolicy.TrustedProxiesKey;

    internal const int DefaultSessionsPerMinutePerIp = 20;
    internal const int DefaultMessagesPerMinutePerIp = 120;

    /// <summary>The surface named in the limiter's log lines and 429 detail.</summary>
    internal const string Surface = "WebChat";

    /// <summary>The tenant part of the partition key for a request that names no known tenant or session.</summary>
    internal const string UnknownTenant = AnonymousRateLimitPolicy.UnknownTenant;

    internal static readonly TimeSpan Window = AnonymousRateLimitPolicy.Window;

    /// <summary>
    /// Limits the endpoint per tenant and client address. <paramref name="resolveTenant"/> returns the
    /// tenant the bound request proves, or <see langword="null"/> when it names no known one.
    /// </summary>
    internal static RouteHandlerBuilder RequireWebChatRateLimit(
        this RouteHandlerBuilder builder,
        string policy,
        string limitKey,
        int defaultLimit,
        Func<EndpointFilterInvocationContext, ValueTask<string?>> resolveTenant) =>
        builder.RequireAnonymousRateLimit(new AnonymousRateLimit(policy, limitKey, defaultLimit, Surface), resolveTenant);

    /// <inheritdoc cref="AnonymousRateLimitPolicy.PartitionKey"/>
    internal static string PartitionKey(string policy, string? tenant, IPAddress? peer, int permitLimit) =>
        AnonymousRateLimitPolicy.PartitionKey(policy, tenant, peer, permitLimit);

    /// <inheritdoc cref="AnonymousRateLimitPolicy.ResolveClientKey"/>
    internal static string ResolveClientKey(IPAddress? address) => AnonymousRateLimitPolicy.ResolveClientKey(address);

    /// <inheritdoc cref="AnonymousRateLimitPolicy.IsLikelyUntrustedProxy"/>
    internal static bool IsLikelyUntrustedProxy(IPAddress? peer, bool trustedProxiesConfigured) =>
        AnonymousRateLimitPolicy.IsLikelyUntrustedProxy(peer, trustedProxiesConfigured);

    /// <summary>The untrusted-proxy warning for the WebChat surface.</summary>
    internal sealed class UntrustedProxyWarning(ILogger logger, bool trustedProxiesConfigured)
        : AnonymousRateLimitPolicy.UntrustedProxyWarning(logger, trustedProxiesConfigured, Surface);
}
