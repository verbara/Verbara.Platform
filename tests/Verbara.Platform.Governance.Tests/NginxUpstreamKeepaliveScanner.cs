using System.Globalization;

namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// Pure, I/O-free check that an nginx configuration reuses its connections to the services behind it. Without
/// reuse every proxied request opens a new TCP connection, and each closed one holds a local port in TIME_WAIT
/// for about 60 seconds; under sustained load the proxy runs out of ephemeral ports and answers 502 until
/// enough of them expire. nginx reuses a connection only when all of these hold:
/// <list type="bullet">
/// <item>the <c>upstream</c> block keeps a pool (<c>keepalive N;</c>);</item>
/// <item>the location speaks HTTP/1.1 to it (<c>proxy_http_version 1.1;</c>, nginx's default is 1.0);</item>
/// <item>the location replaces the <c>Connection</c> header, either with an empty value or with a variable a
/// <c>map $http_upgrade</c> sets to empty when the client asks for no upgrade. Without the header nginx sends
/// <c>Connection: close</c>; a literal <c>"upgrade"</c> sends it on every plain request too, and none of those
/// connections returns to the pool.</item>
/// </list>
/// </summary>
internal static class NginxUpstreamKeepaliveScanner
{
    /// <summary>Every way the configuration can open a new upstream connection per request; empty when it cannot.</summary>
    public static IReadOnlyList<string> Scan(string config)
    {
        var directives = NginxAccessLogScanner.Parse(config);
        var violations = new List<string>();

        var upstreams = NginxAccessLogScanner.Descendants(directives)
            .Where(d => d.Name == "upstream" && d.Children is not null && d.Args.Count == 1)
            .ToList();
        foreach (var upstream in upstreams)
        {
            var keepalive = upstream.Children!.Where(d => d.Name == "keepalive").ToList();
            if (keepalive is not [{ Args: [var size] }]
                || !int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var connections)
                || connections < 1)
            {
                violations.Add(
                    $"upstream '{upstream.Args[0]}' (line {upstream.Line}) declares no 'keepalive N;' with N >= 1, so " +
                    "nginx opens a new connection to it for every request.");
            }
        }

        var upstreamNames = upstreams.Select(u => u.Args[0]).ToHashSet(StringComparer.Ordinal);
        var upgradeVariables = UpgradeAwareConnectionVariables(directives);

        foreach (var (location, server) in Locations(directives, server: null))
        {
            var proxyPass = location.Children!.FirstOrDefault(d => d.Name == "proxy_pass");
            if (proxyPass is not { Args: [var target] } || !upstreamNames.Contains(UpstreamName(target)))
                continue;

            var where = $"location '{string.Join(' ', location.Args)}' (line {location.Line})";

            var version = location.Children!.LastOrDefault(d => d.Name == "proxy_http_version")
                ?? server?.Children!.LastOrDefault(d => d.Name == "proxy_http_version");
            if (version is not { Args: ["1.1"] })
            {
                violations.Add(
                    $"{where} does not set 'proxy_http_version 1.1;', so nginx speaks HTTP/1.0 to " +
                    $"'{UpstreamName(target)}' and closes the connection after every request.");
            }

            // proxy_set_header is inherited all-or-nothing: only a location that sets none gets the server's.
            var headers = location.Children!.Where(d => d.Name == "proxy_set_header").ToList();
            if (headers.Count == 0 && server is not null)
                headers = server.Children!.Where(d => d.Name == "proxy_set_header").ToList();

            var connection = headers
                .Where(d => d.Args.Count == 2 && string.Equals(d.Args[0], "Connection", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (connection.Count == 0)
            {
                violations.Add(
                    $"{where} does not replace the Connection header, so nginx sends 'Connection: close' and " +
                    "the connection never returns to the pool.");
            }
            else if (connection is not [{ Args: [_, var value] }]
                || (value.Length != 0 && !upgradeVariables.Contains(value)))
            {
                violations.Add(
                    $"{where} sets Connection to '{connection[^1].Args[1]}', which every plain request carries " +
                    "too; set it to \"\" or to a 'map $http_upgrade' variable that is empty without an upgrade.");
            }
        }

        return violations;
    }

    /// <summary>
    /// The variables a <c>map $http_upgrade $var</c> sets to empty when the request asks for no upgrade, and to
    /// <c>upgrade</c> otherwise.
    /// </summary>
    private static HashSet<string> UpgradeAwareConnectionVariables(IEnumerable<NginxDirective> directives) =>
        NginxAccessLogScanner.Descendants(directives)
            .Where(d => d.Name == "map" && d.Children is not null && d.Args is ["$http_upgrade", _])
            .Where(map => map.Children!.Any(e => e.Name.Length == 0 && e.Args is [""])
                && map.Children!.Any(e => e.Name == "default" && e.Args is ["upgrade"])
                && map.Children!.All(e => e.Args is [var v] && (v.Length == 0 || v == "upgrade")))
            .Select(map => map.Args[1])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every location block with the server block that encloses it (null for a top-level one).</summary>
    private static IEnumerable<(NginxDirective Location, NginxDirective? Server)> Locations(
        IEnumerable<NginxDirective> directives, NginxDirective? server)
    {
        foreach (var directive in directives.Where(d => d.Children is not null))
        {
            var enclosing = directive.Name == "server" ? directive : server;
            if (directive.Name == "location")
                yield return (directive, enclosing);
            foreach (var nested in Locations(directive.Children!, enclosing))
                yield return nested;
        }
    }

    /// <summary>The upstream a <c>proxy_pass</c> target names: <c>http://api/x</c> is <c>api</c>.</summary>
    private static string UpstreamName(string target)
    {
        var rest = target.StartsWith("http://", StringComparison.Ordinal) ? target["http://".Length..]
            : target.StartsWith("https://", StringComparison.Ordinal) ? target["https://".Length..]
            : target;
        var end = rest.IndexOfAny(['/', ':']);
        return end < 0 ? rest : rest[..end];
    }
}
