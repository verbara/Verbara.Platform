namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// Tree scan of the shipped nginx gateway (<c>docker/nginx-gateway.conf</c>, mounted by the full,
/// production and reference-SMB compose files): every access log it writes must use a format that
/// carries no credential, and no server block may fall back to the image's default format. Includes
/// liveness self-tests (the scan must actually find the server blocks and access logs) and detector
/// unit tests that pin each way a credential could reach the log.
/// </summary>
public sealed class NginxGatewayAccessLogTests
{
    private const string QueryFreeFormat =
        "log_format clean '$remote_addr [$time_local] \"$request_method $uri $server_protocol\" $status';\n";

    [Fact]
    public void GatewayConf_ShouldWriteAccessLogsOnlyInAQueryFreeFormat()
    {
        var violations = NginxAccessLogScanner.Scan(File.ReadAllText(GatewayConfPath()));

        violations.Should().BeEmpty(
            "the SSE stream, the SignalR hub, the OIDC callback and the reset link carry secrets in the query " +
            "string or the Referer, and whoever reads the gateway's logs must not get them");
    }

    [Fact]
    public void GatewayConf_ShouldDeclareAnAccessLogInEveryServerBlock_WhenParsed()
    {
        // A server block, not the `server host:port;` entries inside an upstream block.
        var directives = NginxAccessLogScanner.Parse(File.ReadAllText(GatewayConfPath()));
        var servers = NginxAccessLogScanner.Descendants(directives)
            .Where(d => d.Name == "server" && d.Children is not null)
            .ToList();

        servers.Should().NotBeEmpty("the scan must reach the gateway's server block; finding none means the parser broke");
        servers.Should().OnlyContain(
            server => server.Children!.Any(d => d.Name == "access_log" && d.Args.Count == 2),
            "each server must name its own query-free format rather than inherit the image's default");
    }

    [Fact]
    public void Scan_ShouldAccept_WhenEveryServerLogsInAQueryFreeFormat()
    {
        var config = QueryFreeFormat + "server { listen 80; access_log /var/log/nginx/access.log clean; }";

        NginxAccessLogScanner.Scan(config).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldAccept_WhenAServerTurnsAccessLoggingOff()
    {
        NginxAccessLogScanner.Scan("server { listen 80; access_log off; }").Should().BeEmpty();
    }

    [Theory]
    [InlineData("$request", "request")]
    [InlineData("${request}", "request")]
    [InlineData("$request_uri", "request_uri")]
    [InlineData("$uri$is_args$args", "args")]
    [InlineData("$query_string", "query_string")]
    [InlineData("$arg_token", "arg_token")]
    [InlineData("$arg_access_token", "arg_access_token")]
    [InlineData("$http_referer", "http_referer")]
    [InlineData("$http_authorization", "http_authorization")]
    [InlineData("$cookie_session", "cookie_session")]
    [InlineData("$request_body", "request_body")]
    [InlineData("$sent_http_location", "sent_http_location")]
    public void Scan_ShouldFlag_WhenAFormatWritesACredentialBearingVariable(string variable, string reported)
    {
        var config = $"log_format leaky '$remote_addr {variable} $status';\n" +
            "server { listen 80; access_log /var/log/nginx/access.log leaky; }";

        NginxAccessLogScanner.Scan(config).Should().ContainSingle().Which.Should().Contain($"writes ${reported}");
    }

    [Theory]
    [InlineData("$request_method")]
    [InlineData("$request_time")]
    [InlineData("$request_length")]
    [InlineData("$request_id")]
    [InlineData("$uri")]
    [InlineData("$http_user_agent")]
    public void Scan_ShouldNotFlag_WhenAFormatWritesAVariableWithoutTheQuery(string variable)
    {
        var config = $"log_format fine '$remote_addr {variable} $status';\n" +
            "server { listen 80; access_log /var/log/nginx/access.log fine; }";

        NginxAccessLogScanner.Scan(config).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAnAccessLogNamesNoFormat()
    {
        // Without a format name nginx writes 'combined', which logs $request.
        var violations = NginxAccessLogScanner.Scan("server { listen 80; access_log /var/log/nginx/access.log; }");

        violations.Should().ContainSingle().Which.Should().Contain("names no log_format");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAnAccessLogNamesAFormatThisFileDoesNotDefine()
    {
        var violations = NginxAccessLogScanner.Scan("server { listen 80; access_log /var/log/nginx/access.log main; }");

        violations.Should().ContainSingle().Which.Should().Contain("log_format 'main', which this file does not define");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAServerDeclaresNoAccessLog()
    {
        var violations = NginxAccessLogScanner.Scan(QueryFreeFormat + "server { listen 80; location / { return 204; } }");

        violations.Should().ContainSingle().Which.Should().Contain("declares no access_log of its own");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenOnlyALocationDeclaresTheAccessLog()
    {
        // The other locations of that server still inherit the image's default format.
        var config = QueryFreeFormat +
            "server { listen 80; location /api/ { access_log /var/log/nginx/access.log clean; } location / { return 204; } }";

        NginxAccessLogScanner.Scan(config).Should().ContainSingle().Which.Should().Contain("declares no access_log of its own");
    }

    [Fact]
    public void Parse_ShouldKeepAHashInsideAQuotedString_WhenItLooksLikeAComment()
    {
        var directives = NginxAccessLogScanner.Parse(
            "map $http_referer $referer_path { default \"\"; \"~^(?<p>[^?#]*)\" $p; } # trailing comment\n");

        var map = directives.Should().ContainSingle().Subject;
        map.Children.Should().HaveCount(2);
        map.Children![1].Name.Should().Be("~^(?<p>[^?#]*)");
        map.Children[1].Args.Should().Equal("$p");
    }

    [Fact]
    public void Parse_ShouldJoinAMultiLineFormat_WhenItsStringsSpanSeveralLines()
    {
        var config = "log_format split '$remote_addr '\n    '$request';\n" +
            "server { listen 80; access_log /var/log/nginx/access.log split; }";

        NginxAccessLogScanner.Scan(config).Should().ContainSingle().Which.Should().Contain("writes $request");
    }

    private static string GatewayConfPath()
    {
        var repoRoot = Directory.GetParent(TestTreeSource.TestsRoot())!.FullName;
        var path = Path.Join(repoRoot, "docker", "nginx-gateway.conf");
        File.Exists(path).Should().BeTrue($"the gateway configuration must exist at {path}");
        return path;
    }
}
