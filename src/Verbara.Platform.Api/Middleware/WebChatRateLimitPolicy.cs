using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Rate limits for the anonymous WebChat REST endpoints (session create, message send).
/// <para>
/// Each limit is partitioned per tenant and per client address, so one noisy client cannot exhaust
/// a budget other visitors share, and one tenant's traffic cannot exhaust another tenant's even when
/// every visitor reaches the app from the same proxy address. The tenant is the one the request
/// proves: for a session create, the requested tenant when it exists; for a message, the tenant of
/// the session it names. A request for an unknown tenant or session lands in one shared "unknown"
/// partition per client address, so naming a new one each time neither escapes the limit nor spends
/// a real tenant's budget. Because the session-create tenant is only in the body, the limit runs as
/// an endpoint filter after binding, with an explicit <see cref="PartitionedRateLimiter{TResource}"/>.
/// </para>
/// <para>
/// The client address is <see cref="ConnectionInfo.RemoteIpAddress"/>, i.e. the one the app trusts:
/// <c>UseForwardedHeaders</c> (configured from <c>ForwardedHeaders:TrustedProxies</c>) rewrites it
/// only for a request that arrives through a trusted proxy. IPv6 clients are grouped by their /64
/// prefix, the smallest block a single subscriber is normally assigned. With no trusted proxy and a
/// private or loopback peer, the app logs one warning naming the setting
/// (<see cref="UntrustedProxyWarning"/>): every visitor then shares the proxy's address and, per
/// tenant, one budget.
/// </para>
/// <para>
/// Limits come from configuration (fixed one-minute windows):
/// <c>WebChat:RateLimit:SessionsPerMinutePerIp</c> (default <see cref="DefaultSessionsPerMinutePerIp"/>)
/// and <c>WebChat:RateLimit:MessagesPerMinutePerIp</c> (default <see cref="DefaultMessagesPerMinutePerIp"/>).
/// A rejected request gets the shared 429 response with <c>Retry-After</c>
/// (<see cref="TenantRateLimitPolicy.WriteTooManyRequestsAsync"/>).
/// </para>
/// </summary>
internal static partial class WebChatRateLimitPolicy
{
    internal const string SessionsPolicy = "webchat-sessions";
    internal const string MessagesPolicy = "webchat-messages";

    internal const string SessionsLimitKey = "WebChat:RateLimit:SessionsPerMinutePerIp";
    internal const string MessagesLimitKey = "WebChat:RateLimit:MessagesPerMinutePerIp";
    internal const string TrustedProxiesKey = "ForwardedHeaders:TrustedProxies";

    internal const int DefaultSessionsPerMinutePerIp = 20;
    internal const int DefaultMessagesPerMinutePerIp = 120;

    /// <summary>The tenant part of the partition key for a request that names no known tenant or session.</summary>
    internal const string UnknownTenant = "-";

    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Limits the endpoint per tenant and client address. <paramref name="resolveTenant"/> returns the
    /// tenant the bound request proves, or <see langword="null"/> when it names no known one.
    /// </summary>
    internal static RouteHandlerBuilder RequireWebChatRateLimit(
        this RouteHandlerBuilder builder,
        string policy,
        string limitKey,
        int defaultLimit,
        Func<EndpointFilterInvocationContext, ValueTask<string?>> resolveTenant)
    {
        return builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            var services = factoryContext.ApplicationServices;
            var configuration = services.GetRequiredService<IConfiguration>();
            var warning = new UntrustedProxyWarning(
                services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebChatRateLimitPolicy).FullName!),
                HasTrustedProxies(configuration));

            var limiter = PartitionedRateLimiter.Create<string, string>(key =>
            {
                // The limit is the key's last segment, so a changed setting takes effect on a fresh bucket.
                var permitLimit = int.Parse(key.AsSpan(key.LastIndexOf('|') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    Window = Window,
                    PermitLimit = permitLimit,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
            services.GetService<IHostApplicationLifetime>()?.ApplicationStopped.Register(limiter.Dispose);

            return async invocation =>
            {
                var http = invocation.HttpContext;
                var peer = http.Connection.RemoteIpAddress;
                warning.Observe(peer);

                var tenant = await resolveTenant(invocation);
                var key = PartitionKey(policy, tenant, peer, ReadLimit(configuration, limitKey, defaultLimit));

                using var lease = await limiter.AcquireAsync(key, 1, http.RequestAborted);
                if (lease.IsAcquired)
                    return await next(invocation);

                await TenantRateLimitPolicy.WriteTooManyRequestsAsync(
                    http, lease, "WebChat rate limit exceeded", http.RequestAborted);
                return Results.Empty;
            };
        });
    }

    /// <summary>The partition key: policy, tenant (or <see cref="UnknownTenant"/>), client key and limit.</summary>
    internal static string PartitionKey(string policy, string? tenant, IPAddress? peer, int permitLimit) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{policy}|{(string.IsNullOrEmpty(tenant) ? UnknownTenant : tenant)}|{ResolveClientKey(peer)}|{permitLimit}");

    private static int ReadLimit(IConfiguration configuration, string key, int defaultLimit)
    {
        var raw = configuration[key];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : defaultLimit;
    }

    private static bool HasTrustedProxies(IConfiguration configuration) =>
        configuration.GetSection(TrustedProxiesKey).GetChildren().Any(c => !string.IsNullOrWhiteSpace(c.Value));

    /// <summary>
    /// The partition key for a client address: IPv4 (including IPv4-mapped IPv6) as is, IPv6 by
    /// its /64 prefix, and a single shared key when the address is unknown.
    /// </summary>
    internal static string ResolveClientKey(IPAddress? address)
    {
        if (address is null)
            return "unknown";

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return string.Create(CultureInfo.InvariantCulture, $"{new IPAddress(bytes)}/64");
    }

    /// <summary>
    /// Whether a request from <paramref name="peer"/> most likely came through a proxy the app does
    /// not trust: no proxy is trusted and the peer is a loopback or private address (RFC 1918, IPv6
    /// unique-local or link-local), where reverse proxies and container networks live.
    /// </summary>
    internal static bool IsLikelyUntrustedProxy(IPAddress? peer, bool trustedProxiesConfigured)
    {
        if (trustedProxiesConfigured || peer is null)
            return false;

        if (peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();

        if (IPAddress.IsLoopback(peer))
            return true;

        if (peer.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            peer.TryWriteBytes(b, out _);
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168);
        }

        return peer.IsIPv6UniqueLocal || peer.IsIPv6LinkLocal;
    }

    /// <summary>
    /// Logs one warning, the first time a WebChat request arrives from what looks like an untrusted
    /// proxy (<see cref="IsLikelyUntrustedProxy"/>), naming the setting that makes the limits per visitor.
    /// </summary>
    internal sealed partial class UntrustedProxyWarning(ILogger logger, bool trustedProxiesConfigured)
    {
        private int _logged;

        public void Observe(IPAddress? peer)
        {
            if (Volatile.Read(ref _logged) != 0 || !IsLikelyUntrustedProxy(peer, trustedProxiesConfigured))
                return;

            if (Interlocked.Exchange(ref _logged, 1) == 0)
                LogUntrustedProxy(logger, peer!.ToString());
        }

        [LoggerMessage(EventId = 4290, Level = LogLevel.Warning,
            Message = "WebChat requests arrive from the private address {Peer} and " + TrustedProxiesKey +
                " is empty, so the app cannot see the visitors' addresses: the WebChat rate limits apply per " +
                "proxy and tenant, not per visitor. Set " + TrustedProxiesKey + " to the proxy's address or network.")]
        private static partial void LogUntrustedProxy(ILogger logger, string peer);
    }
}
