// LoadTestHttpClient — centralised HttpClient factory for NBomber scenarios.
//
// Purpose: when the LoadTests harness runs against a K8s Gateway API ingress
// (R5.5 Phase B-LK), the Gateway routes requests via HTTPRoute hostname
// matching. The HTTPRoute spec disallows IP literals in `hostnames`, so the
// loadtest cannot point at `http://<gateway-ip>/` directly — the request
// arrives with `Host: <gateway-ip>` and the Gateway returns 404.
//
// Two operating modes:
//
//   1. Direct mode (default) — no DNS override; identical to the original
//      `new HttpClient { BaseAddress = new Uri(baseUrl) }`. Used by Phase B-L
//      Docker baseline (baseUrl is the docker-compose-host-mapped port, no
//      Gateway in the path).
//
//   2. Resolve mode — when env `LOADTEST_RESOLVE` is set in the form
//      `hostname:ip` (curl --resolve syntax), the factory wires a
//      `SocketsHttpHandler.ConnectCallback` that diverts socket connect to
//      the configured IP while preserving the `Host` header derived from
//      `baseUrl`. This lets us run NBomber on the host against
//      `PLATFORM_API_URL=http://api.r55.local/` (which the chart-managed
//      HTTPRoute binds) without touching /etc/hosts (no sudo in CI).
//
// Also tunes the handler for high-concurrency loadtests:
//   - MaxConnectionsPerServer: 1000 (default is int.MaxValue but the
//     pool initialiser is lazy; explicit raises confidence under burst).
//   - PooledConnectionLifetime: 5 min (long enough that NBomber's 2-min
//     scenarios never rotate connections mid-run).
//   - NoDelay: true (avoid Nagle batching for sub-ms request latencies).

using System.Net;
using System.Net.Sockets;

namespace Verbara.Platform.LoadTests.Infrastructure;

internal static class LoadTestHttpClient
{
    public static HttpClient Create(string baseUrl)
    {
        var resolve = Environment.GetEnvironmentVariable("LOADTEST_RESOLVE");
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1000,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (!string.IsNullOrEmpty(resolve))
        {
            var parts = resolve.Split(':', 2);
            if (parts.Length != 2 || !IPAddress.TryParse(parts[1], out var ip))
            {
                throw new InvalidOperationException(
                    $"LOADTEST_RESOLVE must be `hostname:ip`, got `{resolve}`.");
            }

            var hostname = parts[0];
            handler.ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    var targetIp = string.Equals(context.DnsEndPoint.Host, hostname,
                        StringComparison.OrdinalIgnoreCase) ? ip : null;

                    if (targetIp is null)
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await socket.ConnectAsync(targetIp, context.DnsEndPoint.Port, ct)
                            .ConfigureAwait(false);
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
    }
}
