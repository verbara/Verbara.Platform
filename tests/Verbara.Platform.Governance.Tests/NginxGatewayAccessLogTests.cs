using System.Text.RegularExpressions;

namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// Tree scan of the shipped nginx configurations: every <c>nginx*.conf</c> under <c>docker/</c> and every file
/// a compose file mounts into an nginx container (<c>docker/nginx-gateway.conf</c>, mounted by the full,
/// production, reference-SMB and demo compose files; <c>docker/nginx-loadbalancer.conf</c>, mounted by the
/// scale compose file). Every access log they write must use a format that carries no credential, every
/// error log must write nowhere, and no server block may fall back to the image's default for either. The
/// gateway must also keep the query and the Referer's query from the web image, whose nginx logs both, while
/// passing the query on to the API and the hub, which authenticate with it. Includes liveness self-tests (the
/// scan must actually find the configurations, their server blocks and their logs) and detector unit tests
/// that pin each way a credential could reach a log.
/// </summary>
public sealed partial class NginxGatewayAccessLogTests
{
    private const string GatewayConf = "docker/nginx-gateway.conf";
    private const string LoadBalancerConf = "docker/nginx-loadbalancer.conf";

    private const string QueryFreeFormat =
        "log_format clean '$remote_addr [$time_local] \"$request_method $uri $server_protocol\" $status';\n";

    public static TheoryData<string> ShippedConfs() => new(DiscoverShippedConfs());

    [Theory]
    [MemberData(nameof(ShippedConfs))]
    public void ShippedConf_ShouldWriteAccessLogsOnlyInAQueryFreeFormat(string conf)
    {
        var violations = NginxAccessLogScanner.Scan(ReadRepoFile(conf));

        violations.Should().BeEmpty(
            "the SSE stream, the SignalR hub, the OIDC callback and the reset link carry secrets in the query " +
            "string or the Referer, and whoever reads these logs must not get them");
    }

    [Theory]
    [MemberData(nameof(ShippedConfs))]
    public void ShippedConf_ShouldWriteNoRequestLineToAnErrorLog(string conf)
    {
        var violations = NginxAccessLogScanner.ScanErrorLogs(ReadRepoFile(conf));

        violations.Should().BeEmpty(
            "nginx adds the request line, the upstream URL and the Referer, query strings included, to every " +
            "message it logs while it handles a request, at every level, and no directive changes that text");
    }

    [Theory]
    [MemberData(nameof(ShippedConfs))]
    public void ShippedConf_ShouldDiscardTheErrorLogInEveryServerBlock_WhenParsed(string conf)
    {
        var servers = ServerBlocks(ReadRepoFile(conf));

        servers.Should().NotBeEmpty(
            "the scan must reach each configuration's server block; finding none means the parser broke");
        servers.Should().OnlyContain(
            server => server.Children!.Any(d => d.Name == "error_log" && d.Args.Count > 0 && d.Args[0] == "/dev/null"),
            "each server must replace the image's error log rather than inherit it");
    }

    [Fact]
    public void GatewayConf_ShouldDeclareAnAccessLogInEveryServerBlock_WhenParsed()
    {
        var servers = ServerBlocks(ReadRepoFile(GatewayConf));

        servers.Should().NotBeEmpty("the scan must reach the gateway's server block; finding none means the parser broke");
        servers.Should().OnlyContain(
            server => server.Children!.Any(d => d.Name == "access_log" && d.Args.Count == 2),
            "each server must name its own query-free format rather than inherit the image's default");
    }

    [Fact]
    public void GatewayConf_ShouldPassTheWebImageNeitherTheQueryNorTheReferersQuery()
    {
        var web = GatewayLocation("/");

        web.Children.Should().Contain(
            d => d.Name == "rewrite" && d.Args.Count == 3 && d.Args[1].EndsWith('?') && d.Args[2] == "break",
            "the web image's nginx logs the raw request line, and the console reads the reset link's token in the browser");
        web.Children.Should().Contain(
            d => d.Name == "proxy_set_header" && d.Args.Count == 2 && d.Args[0] == "Referer"
                && d.Args[1] == "$verbara_referer_path",
            "the web image's nginx logs the raw Referer, which carries the reset page's token");
    }

    [Theory]
    [InlineData("/api/")]
    [InlineData("/hubs/")]
    public void GatewayConf_ShouldPassTheQueryOnToTheApiAndTheHub(string path)
    {
        var location = GatewayLocation(path);

        location.Children.Should().NotContain(d => d.Name == "rewrite",
            "the SSE stream and the hub authenticate with the token in the query");
        location.Children.Should().ContainSingle(d => d.Name == "proxy_pass").Which.Args.Should().ContainSingle()
            .Which.Should().NotContain("$",
                "a proxy_pass built from variables passes only what they name, not the request's query");
    }

    [Fact]
    public void ShippedConfs_ShouldIncludeTheGatewayAndTheLoadBalancer_WhenDiscovered()
    {
        DiscoverShippedConfs().Should().Contain([GatewayConf, LoadBalancerConf],
            "the discovery must reach the shipped configurations; finding fewer means it broke");
    }

    [Fact]
    public void MountedConfs_ShouldFindTheGatewayAndTheLoadBalancer_WhenTheComposeFilesAreScanned()
    {
        var mounted = MountedConfs();

        mounted.Should().Contain([GatewayConf, LoadBalancerConf],
            "the compose files mount both configurations; finding fewer means the mount discovery broke");
        mounted.Should().OnlyContain(conf => File.Exists(RepoPath(conf)),
            "a compose file must not mount a configuration that is not in the tree");
    }

    [Fact]
    public void NginxMountSources_ShouldFindOnlyBindMountsIntoNginx_WhenAComposeFileHasOthers()
    {
        const string compose =
            "    volumes:\n" +
            "      - ./nginx-gateway.conf:/etc/nginx/conf.d/default.conf:ro\n" +
            "      - \"../edge.conf:/etc/nginx/nginx.conf:ro\"\n" +
            "      # - ./disabled.conf:/etc/nginx/conf.d/default.conf:ro\n" +
            "      - ./asterisk-config:/etc/asterisk\n" +
            "      - recordings:/recordings\n";

        NginxMountSources(compose).Should().Equal("./nginx-gateway.conf", "../edge.conf");
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

    [Theory]
    [InlineData("error_log /dev/null;")]
    [InlineData("error_log /dev/null crit;")]
    public void ScanErrorLogs_ShouldAccept_WhenEveryServerDiscardsItsErrorLog(string errorLog)
    {
        var config = $"server {{ listen 80; {errorLog} location / {{ proxy_pass http://web; }} }}";

        NginxAccessLogScanner.ScanErrorLogs(config).Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "error", "every failed proxied request")]
    [InlineData(" debug", "debug", "every failed proxied request (error) and every response or request body buffered")]
    [InlineData(" info", "info", "every failed proxied request (error) and every response or request body buffered")]
    [InlineData(" notice", "notice", "every failed proxied request (error) and every response or request body buffered")]
    [InlineData(" warn", "warn", "every failed proxied request (error) and every response or request body buffered")]
    [InlineData(" error", "error", "every failed proxied request: connection refused or reset")]
    [InlineData(" crit", "crit", "a temporary file nginx cannot write (crit)")]
    [InlineData(" alert", "alert", "worker_connections exhausted while nginx connects to an upstream (alert)")]
    [InlineData(" emerg", "emerg", "a memory allocation that fails while nginx handles a request (emerg)")]
    public void ScanErrorLogs_ShouldFlag_WhenAnErrorLogWritesAtAnyLevel(string level, string reported, string reason)
    {
        // No level is safe: at every one, some message about a request (with its request line) is still written.
        var config = $"server {{ listen 80; error_log /var/log/nginx/error.log{level}; }}";

        NginxAccessLogScanner.ScanErrorLogs(config).Should().ContainSingle()
            .Which.Should().Contain($"error_log '/var/log/nginx/error.log' at level '{reported}'").And.Contain(reason);
    }

    [Theory]
    [InlineData("stderr")]
    [InlineData("/dev/stderr")]
    [InlineData("syslog:server=unix:/dev/log")]
    [InlineData("memory:32m")]
    [InlineData("/var/log/nginx/gateway-error.log")]
    public void ScanErrorLogs_ShouldFlag_WhenAnErrorLogWritesToAnyOtherDestination(string destination)
    {
        var config = $"server {{ listen 80; error_log {destination} crit; }}";

        NginxAccessLogScanner.ScanErrorLogs(config).Should().ContainSingle()
            .Which.Should().Contain($"error_log '{destination}' at level 'crit'");
    }

    [Fact]
    public void ScanErrorLogs_ShouldFlag_WhenAServerDeclaresNoErrorLog()
    {
        var violations = NginxAccessLogScanner.ScanErrorLogs("server { listen 80; location / { proxy_pass http://web; } }");

        violations.Should().ContainSingle().Which.Should().Contain("declares no error_log of its own");
    }

    [Fact]
    public void ScanErrorLogs_ShouldFlag_WhenOnlyALocationDiscardsTheErrorLog()
    {
        // nginx reads the request, and runs the server's other locations, under the server's error log.
        var config = "server { listen 80; location /api/ { error_log /dev/null; } location / { proxy_pass http://web; } }";

        NginxAccessLogScanner.ScanErrorLogs(config).Should().ContainSingle()
            .Which.Should().Contain("declares no error_log of its own");
    }

    [Fact]
    public void ScanErrorLogs_ShouldFlag_WhenALocationWritesTheErrorLogTheServerDiscards()
    {
        var config = "server { listen 80; error_log /dev/null; location /api/ { error_log /var/log/nginx/error.log; } }";

        NginxAccessLogScanner.ScanErrorLogs(config).Should().ContainSingle()
            .Which.Should().Contain("error_log '/var/log/nginx/error.log' at level 'error' (line 1)");
    }

    [Fact]
    public void ScanErrorLogs_ShouldFlag_WhenTheIncludingHttpLevelGetsAnErrorLog()
    {
        // This file's top level is the image's http block: an error_log there writes for every server that
        // declares none, alongside any other error_log on that level.
        var config = "error_log /var/log/nginx/error.log;\nserver { listen 80; error_log /dev/null; }";

        NginxAccessLogScanner.ScanErrorLogs(config).Should().ContainSingle().Which.Should().Contain("(line 1)");
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

    /// <summary>
    /// The shipped configurations, as repository-relative paths: every <c>nginx*.conf</c> under <c>docker/</c>,
    /// and every file a compose file mounts into nginx.
    /// </summary>
    private static List<string> DiscoverShippedConfs()
    {
        var named = Directory.EnumerateFiles(RepoPath("docker"), "nginx*.conf", SearchOption.AllDirectories)
            .Select(ToRepoRelative);

        return named.Concat(MountedConfs()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The files the repository's compose files bind-mount under <c>/etc/nginx/</c>, repository-relative.</summary>
    private static List<string> MountedConfs()
    {
        var composeFiles = Directory.EnumerateFiles(RepoPath(""), "docker-compose*.y*ml")
            .Concat(Directory.EnumerateFiles(RepoPath("docker"), "docker-compose*.y*ml", SearchOption.AllDirectories));

        return composeFiles
            .SelectMany(compose => NginxMountSources(File.ReadAllText(compose))
                .Select(source => ToRepoRelative(Path.GetFullPath(Path.Join(Path.GetDirectoryName(compose), source)))))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The source of each short-syntax volume entry that mounts into <c>/etc/nginx/</c>.</summary>
    private static List<string> NginxMountSources(string compose) =>
        NginxBindMount().Matches(compose).Select(match => match.Groups["source"].Value).ToList();

    // `- ./nginx-gateway.conf:/etc/nginx/conf.d/default.conf:ro`, optionally quoted; a commented-out entry
    // starts with `#` and does not match.
    [GeneratedRegex("""^\s*-\s*["']?(?<source>[^\s:"']+):/etc/nginx/""", RegexOptions.Multiline)]
    private static partial Regex NginxBindMount();

    private static List<NginxDirective> ServerBlocks(string config) =>
        NginxAccessLogScanner.Descendants(NginxAccessLogScanner.Parse(config))
            .Where(d => d.Name == "server" && d.Children is not null) // not the `server host:port;` entries of an upstream
            .ToList();

    private static NginxDirective GatewayLocation(string path)
    {
        var locations = NginxAccessLogScanner.Descendants(NginxAccessLogScanner.Parse(ReadRepoFile(GatewayConf)))
            .Where(d => d.Name == "location" && d.Args is [var prefix] && prefix == path && d.Children is not null)
            .ToList();
        locations.Should().ContainSingle($"the gateway must route '{path}' in exactly one location");
        return locations[0];
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = RepoPath(relativePath);
        File.Exists(path).Should().BeTrue($"the configuration must exist at {path}");
        return File.ReadAllText(path);
    }

    private static string RepoPath(string relativePath) =>
        Path.Join(Directory.GetParent(TestTreeSource.TestsRoot())!.FullName, relativePath);

    private static string ToRepoRelative(string path) =>
        Path.GetRelativePath(RepoPath(""), path).Replace(Path.DirectorySeparatorChar, '/');
}
