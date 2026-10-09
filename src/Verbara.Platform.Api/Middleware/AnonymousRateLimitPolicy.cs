using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// One endpoint-filter rate limit for anonymous routes: its policy name, the configuration key that
/// sets its per-minute limit and the default, and the surface named in its log lines.
/// </summary>
/// <remarks>
/// With <see cref="UnknownTenantLimitKey"/> set, a request the endpoint answers <c>404</c> (its tenant
/// has nothing configured) is additionally charged to one bucket per client shared by every unknown
/// tenant, so naming a new tenant each time neither escapes the limit nor spends a real tenant's budget.
/// </remarks>
internal sealed record AnonymousRateLimit(string Policy, string LimitKey, int DefaultLimit, string Surface)
{
    public string? UnknownTenantLimitKey { get; init; }

    public int UnknownTenantDefaultLimit { get; init; }
}

/// <summary>
/// The endpoint-filter rate limiter for anonymous routes (the WebChat REST endpoints and the provider
/// webhooks), generalised in v2.27.0 from the 2.26.1 WebChat limiter.
/// <para>
/// Each limit is partitioned per tenant and per client address, so one noisy client cannot exhaust a
/// budget other clients share, and one tenant's traffic cannot exhaust another tenant's even when every
/// client reaches the app from the same proxy address. The tenant comes from a caller-supplied resolver;
/// a request that names no known tenant lands in one shared <see cref="UnknownTenant"/> partition per
/// client address. The limit runs as an endpoint filter (after binding), with an explicit
/// <see cref="PartitionedRateLimiter{TResource}"/> shared by every endpoint the builder covers.
/// </para>
/// <para>
/// The client address is <see cref="ConnectionInfo.RemoteIpAddress"/>, i.e. the one the app trusts:
/// <c>UseForwardedHeaders</c> (configured from <c>ForwardedHeaders:TrustedProxies</c>) rewrites it only
/// for a request that arrives through a trusted proxy. IPv6 clients are grouped by their /64 prefix,
/// the smallest block a single subscriber is normally assigned. With no trusted proxy and a private or
/// loopback peer, the app logs one warning naming the setting (<see cref="UntrustedProxyWarning"/>).
/// </para>
/// <para>
/// Limits are fixed one-minute windows read from configuration. A rejected request gets the shared 429
/// response with <c>Retry-After</c> (<see cref="TenantRateLimitPolicy.WriteTooManyRequestsAsync"/>), and
/// the first rejection per tenant partition is logged.
/// </para>
/// </summary>
internal static partial class AnonymousRateLimitPolicy
{
    internal const string TrustedProxiesKey = "ForwardedHeaders:TrustedProxies";

    /// <summary>The tenant part of the partition key for a request that names no known tenant.</summary>
    internal const string UnknownTenant = "-";

    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound on the tenants remembered for the "first 429" log line.</summary>
    private const int MaxLoggedRejections = 4096;

    /// <summary>
    /// Limits every endpoint <paramref name="builder"/> covers per tenant and client address.
    /// <paramref name="resolveTenant"/> returns the tenant the request names, or <see langword="null"/>
    /// when it names no known one. Every endpoint of the builder shares one limiter, so a route group's
    /// <c>GET</c> and <c>POST</c> spend the same budget.
    /// </summary>
    internal static TBuilder RequireAnonymousRateLimit<TBuilder>(
        this TBuilder builder,
        AnonymousRateLimit limit,
        Func<EndpointFilterInvocationContext, ValueTask<string?>> resolveTenant)
        where TBuilder : IEndpointConventionBuilder
    {
        SharedState? state = null;
        var gate = new Lock();

        return builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            lock (gate)
                state ??= new SharedState(factoryContext.ApplicationServices, limit.Surface);

            var shared = state;
            var configuration = shared.Configuration;

            return async invocation =>
            {
                var http = invocation.HttpContext;
                var peer = http.Connection.RemoteIpAddress;
                shared.Warning.Observe(peer);

                var tenant = await resolveTenant(invocation);
                var key = PartitionKey(limit.Policy, tenant, peer, ReadLimit(configuration, limit.LimitKey, limit.DefaultLimit));

                using (var lease = await shared.Limiter.AcquireAsync(key, 1, http.RequestAborted))
                {
                    if (!lease.IsAcquired)
                        return await RejectAsync(shared, limit, tenant, http, lease);
                }

                var result = await next(invocation);

                if (limit.UnknownTenantLimitKey is not null && result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status404NotFound })
                {
                    var unknownKey = PartitionKey(limit.Policy + ":unknown", null, peer,
                        ReadLimit(configuration, limit.UnknownTenantLimitKey, limit.UnknownTenantDefaultLimit));
                    using var unknownLease = await shared.Limiter.AcquireAsync(unknownKey, 1, http.RequestAborted);
                    if (!unknownLease.IsAcquired)
                        return await RejectAsync(shared, limit, null, http, unknownLease);
                }

                return result;
            };
        });
    }

    private static async Task<object?> RejectAsync(
        SharedState shared, AnonymousRateLimit limit, string? tenant, HttpContext http, RateLimitLease lease)
    {
        shared.ObserveRejection(limit, tenant);
        await TenantRateLimitPolicy.WriteTooManyRequestsAsync(
            http, lease, $"{limit.Surface} rate limit exceeded", http.RequestAborted);
        return Results.Empty;
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

    /// <summary>The limiter, warning and logger shared by every endpoint one builder covers.</summary>
    private sealed class SharedState
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, byte> _loggedRejections = new(StringComparer.Ordinal);

        public SharedState(IServiceProvider services, string surface)
        {
            Configuration = services.GetRequiredService<IConfiguration>();
            _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AnonymousRateLimitPolicy).FullName!);
            Warning = new UntrustedProxyWarning(_logger, HasTrustedProxies(Configuration), surface);

            Limiter = PartitionedRateLimiter.Create<string, string>(key =>
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
            services.GetService<IHostApplicationLifetime>()?.ApplicationStopped.Register(Limiter.Dispose);
        }

        public IConfiguration Configuration { get; }

        public PartitionedRateLimiter<string> Limiter { get; }

        public UntrustedProxyWarning Warning { get; }

        /// <summary>Logs the first rejection per policy and tenant (bounded, so enumeration cannot grow it).</summary>
        public void ObserveRejection(AnonymousRateLimit limit, string? tenant)
        {
            var tenantKey = string.IsNullOrEmpty(tenant) ? UnknownTenant : tenant;
            var key = limit.Policy + "|" + tenantKey;
            if (_loggedRejections.Count >= MaxLoggedRejections || !_loggedRejections.TryAdd(key, 0))
                return;

            LogFirstRejection(_logger, limit.Surface, tenantKey, limit.Policy);
        }
    }

    [LoggerMessage(EventId = 4291, Level = LogLevel.Warning,
        Message = "{Surface} rate limit reached for tenant {Tenant} (policy {Policy}); further requests are " +
            "answered 429. Logged once per tenant and policy; raise the limit if this is legitimate traffic.")]
    private static partial void LogFirstRejection(ILogger logger, string surface, string tenant, string policy);

    /// <summary>
    /// Logs one warning, the first time a request arrives from what looks like an untrusted proxy
    /// (<see cref="IsLikelyUntrustedProxy"/>), naming the setting that makes the limits per client.
    /// </summary>
    internal partial class UntrustedProxyWarning(ILogger logger, bool trustedProxiesConfigured, string surface)
    {
        private int _logged;

        public void Observe(IPAddress? peer)
        {
            if (Volatile.Read(ref _logged) != 0 || !IsLikelyUntrustedProxy(peer, trustedProxiesConfigured))
                return;

            if (Interlocked.Exchange(ref _logged, 1) == 0)
                LogUntrustedProxy(logger, surface, peer!.ToString());
        }

        [LoggerMessage(EventId = 4290, Level = LogLevel.Warning,
            Message = "{Surface} requests arrive from the private address {Peer} and " + TrustedProxiesKey +
                " is empty, so the app cannot see the clients' addresses: these rate limits apply per " +
                "proxy and tenant, not per client. Set " + TrustedProxiesKey + " to the proxy's address or network.")]
        private static partial void LogUntrustedProxy(ILogger logger, string surface, string peer);
    }
}
