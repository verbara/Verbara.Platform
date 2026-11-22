namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// Tree scan of the shipped nginx configurations (the same set <see cref="NginxGatewayAccessLogTests"/> discovers):
/// every proxied request must be able to reuse a pooled connection to its upstream. Without that, each request
/// leaves a local port in TIME_WAIT for about 60 seconds, and under sustained load the gateway runs out of
/// ephemeral ports and answers 502 for tens of seconds at a time. Includes liveness self-tests and detector unit
/// tests for each way reuse is lost.
/// </summary>
public sealed class NginxUpstreamKeepaliveTests
{
    private const string GatewayConf = "docker/nginx-gateway.conf";

    private const string UpgradeMap = "map $http_upgrade $connection_upgrade { default upgrade; \"\" \"\"; }\n";

    [Theory]
    [MemberData(nameof(NginxGatewayAccessLogTests.ShippedConfs), MemberType = typeof(NginxGatewayAccessLogTests))]
    public void ShippedConf_ShouldReuseUpstreamConnections(string conf)
    {
        var violations = NginxUpstreamKeepaliveScanner.Scan(ReadRepoFile(conf));

        violations.Should().BeEmpty(
            "a request that cannot reuse a pooled connection leaves a port in TIME_WAIT, and under sustained " +
            "load the gateway runs out of ephemeral ports and answers 502");
    }

    [Fact]
    public void GatewayConf_ShouldKeepAPoolForEveryUpstream_WhenParsed()
    {
        var upstreams = NginxAccessLogScanner.Descendants(NginxAccessLogScanner.Parse(ReadRepoFile(GatewayConf)))
            .Where(d => d.Name == "upstream" && d.Children is not null)
            .ToList();

        upstreams.Select(u => u.Args.Single()).Should().BeEquivalentTo(["realtime", "api", "web"],
            "the scan must reach the gateway's three upstreams; finding fewer means the parser broke");
        upstreams.Should().OnlyContain(u => u.Children!.Any(d => d.Name == "keepalive"));
    }

    [Theory]
    [InlineData("/hubs/")]
    [InlineData("/ws/")]
    public void GatewayConf_ShouldSendConnectionUpgradeOnlyOnAnUpgrade(string path)
    {
        var directives = NginxAccessLogScanner.Parse(ReadRepoFile(GatewayConf));
        var location = NginxAccessLogScanner.Descendants(directives)
            .Single(d => d.Name == "location" && d.Args is [var prefix] && prefix == path);

        location.Children.Should().ContainSingle(d => d.Name == "proxy_set_header" && d.Args.Count == 2
                && d.Args[0] == "Connection")
            .Which.Args[1].Should().Be("$connection_upgrade",
                "a SignalR negotiate POST, long polling and SSE are plain requests: a literal 'upgrade' closes " +
                "their connection instead of returning it to the pool");
        directives.Where(d => d.Name == "map" && d.Args is ["$http_upgrade", "$connection_upgrade"]).Should().ContainSingle(
            "the variable must come from the one map that empties it when the client asks for no upgrade");
    }

    [Fact]
    public void Scan_ShouldAccept_WhenTheUpstreamKeepsAPoolAndTheLocationClearsConnection()
    {
        const string config =
            "upstream api { server platform-api:5000; keepalive 64; }\n" +
            "server { location /api/ { proxy_pass http://api; proxy_http_version 1.1; " +
            "proxy_set_header Connection \"\"; } }";

        NginxUpstreamKeepaliveScanner.Scan(config).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldAccept_WhenALocationSendsTheUpgradeMapVariable()
    {
        var config = UpgradeMap +
            "upstream realtime { server realtime:5030; keepalive 32; }\n" +
            "server { location /hubs/ { proxy_pass http://realtime; proxy_http_version 1.1; " +
            "proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection $connection_upgrade; } }";

        NginxUpstreamKeepaliveScanner.Scan(config).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldAccept_WhenTheLocationInheritsTheServersVersionAndHeaders()
    {
        const string config =
            "upstream web { server web:80; keepalive 8; }\n" +
            "server { proxy_http_version 1.1; proxy_set_header Connection \"\"; location / { proxy_pass http://web; } }";

        NginxUpstreamKeepaliveScanner.Scan(config).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnoreAProxyPassThatNamesNoUpstreamBlock()
    {
        // A plain host:port target is resolved per request and has no pool to keep.
        NginxUpstreamKeepaliveScanner.Scan("server { location / { proxy_pass http://example:8080; } }")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("keepalive 0;")]
    [InlineData("keepalive many;")]
    public void Scan_ShouldFlag_WhenAnUpstreamKeepsNoPool(string keepalive)
    {
        var config = $"upstream api {{ server platform-api:5000; {keepalive} }}\n" +
            "server { location /api/ { proxy_pass http://api; proxy_http_version 1.1; proxy_set_header Connection \"\"; } }";

        NginxUpstreamKeepaliveScanner.Scan(config).Should().ContainSingle()
            .Which.Should().Contain("upstream 'api'").And.Contain("declares no 'keepalive N;'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("proxy_http_version 1.0;")]
    public void Scan_ShouldFlag_WhenALocationSpeaksHttp10ToTheUpstream(string version)
    {
        var config = "upstream api { server platform-api:5000; keepalive 64; }\n" +
            $"server {{ location /health {{ proxy_pass http://api; {version} proxy_set_header Connection \"\"; }} }}";

        NginxUpstreamKeepaliveScanner.Scan(config).Should().ContainSingle()
            .Which.Should().Contain("location '/health'").And.Contain("proxy_http_version 1.1");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenALocationLeavesTheConnectionHeaderToNginx()
    {
        const string config =
            "upstream api { server platform-api:5000; keepalive 64; }\n" +
            "server { location /api/ { proxy_pass http://api; proxy_http_version 1.1; proxy_set_header Host $host; } }";

        NginxUpstreamKeepaliveScanner.Scan(config).Should().ContainSingle()
            .Which.Should().Contain("does not replace the Connection header");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheLocationsOwnHeadersHideTheServersConnection()
    {
        // proxy_set_header is inherited only by a location that sets none of its own.
        const string config =
            "upstream api { server platform-api:5000; keepalive 64; }\n" +
            "server { proxy_set_header Connection \"\"; " +
            "location /api/ { proxy_pass http://api; proxy_http_version 1.1; proxy_set_header Host $host; } }";

        NginxUpstreamKeepaliveScanner.Scan(config).Should().ContainSingle()
            .Which.Should().Contain("does not replace the Connection header");
    }

    [Theory]
    [InlineData("\"upgrade\"", "upgrade")]
    [InlineData("upgrade", "upgrade")]
    [InlineData("close", "close")]
    [InlineData("$http_connection", "$http_connection")]
    public void Scan_ShouldFlag_WhenALocationSendsTheSameConnectionOnEveryRequest(string value, string reported)
    {
        var config = "upstream realtime { server realtime:5030; keepalive 32; }\n" +
            "server { location /hubs/ { proxy_pass http://realtime; proxy_http_version 1.1; " +
            $"proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection {value}; }} }}";

        NginxUpstreamKeepaliveScanner.Scan(config).Should().ContainSingle()
            .Which.Should().Contain($"sets Connection to '{reported}'");
    }

    [Theory]
    [InlineData("map $http_upgrade $connection_upgrade { default upgrade; }\n")]
    [InlineData("map $http_upgrade $connection_upgrade { default upgrade; \"\" close; }\n")]
    [InlineData("map $http_host $connection_upgrade { default upgrade; \"\" \"\"; }\n")]
    public void Scan_ShouldFlag_WhenTheMapDoesNotClearConnectionWithoutAnUpgrade(string map)
    {
        var config = map +
            "upstream realtime { server realtime:5030; keepalive 32; }\n" +
            "server { location /hubs/ { proxy_pass http://realtime; proxy_http_version 1.1; " +
            "proxy_set_header Connection $connection_upgrade; } }";

        NginxUpstreamKeepaliveScanner.Scan(config).Should().ContainSingle()
            .Which.Should().Contain("sets Connection to '$connection_upgrade'");
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.Join(TestTreeSource.RepoRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"the configuration must exist at {path}");
        return File.ReadAllText(path);
    }
}
