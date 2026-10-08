using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Rate limits for the anonymous WebChat REST endpoints (session create, message send).
/// <para>
/// Each policy is partitioned per client address, so one noisy client cannot exhaust a budget
/// other visitors share. The address is <see cref="ConnectionInfo.RemoteIpAddress"/>, i.e. the
/// one the app trusts: <c>UseForwardedHeaders</c> (configured from
/// <c>ForwardedHeaders:TrustedProxies</c>) runs before <c>UseRateLimiter</c> and rewrites it
/// only for a request that arrives through a trusted proxy. IPv6 clients are grouped by their
/// /64 prefix, the smallest block a single subscriber is normally assigned.
/// </para>
/// <para>
/// Limits come from configuration (fixed one-minute windows):
/// <c>WebChat:RateLimit:SessionsPerMinutePerIp</c> (default <see cref="DefaultSessionsPerMinutePerIp"/>)
/// and <c>WebChat:RateLimit:MessagesPerMinutePerIp</c> (default <see cref="DefaultMessagesPerMinutePerIp"/>).
/// A rejected request gets the shared 429 response with <c>Retry-After</c>
/// (<see cref="TenantRateLimitPolicy.ConfigureRateLimiting"/>).
/// </para>
/// </summary>
internal static class WebChatRateLimitPolicy
{
    internal const string SessionsPolicy = "webchat-sessions";
    internal const string MessagesPolicy = "webchat-messages";

    internal const string SessionsLimitKey = "WebChat:RateLimit:SessionsPerMinutePerIp";
    internal const string MessagesLimitKey = "WebChat:RateLimit:MessagesPerMinutePerIp";

    internal const int DefaultSessionsPerMinutePerIp = 20;
    internal const int DefaultMessagesPerMinutePerIp = 120;

    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    internal static void AddPolicies(RateLimiterOptions options)
    {
        options.AddPolicy(SessionsPolicy, context =>
            PerClient(context, SessionsLimitKey, DefaultSessionsPerMinutePerIp));
        options.AddPolicy(MessagesPolicy, context =>
            PerClient(context, MessagesLimitKey, DefaultMessagesPerMinutePerIp));
    }

    private static RateLimitPartition<string> PerClient(HttpContext context, string limitKey, int defaultLimit)
    {
        var permitLimit = ReadLimit(context, limitKey, defaultLimit);
        // The limit is part of the key so a changed setting takes effect on a fresh bucket.
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{ResolveClientKey(context.Connection.RemoteIpAddress)}|{permitLimit}");

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            Window = Window,
            PermitLimit = permitLimit,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    private static int ReadLimit(HttpContext context, string key, int defaultLimit)
    {
        var raw = context.RequestServices.GetService<IConfiguration>()?[key];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : defaultLimit;
    }

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
        return new IPAddress(bytes).ToString() + "/64";
    }
}
