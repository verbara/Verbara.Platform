using System.Text.RegularExpressions;

namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// Every compose service that runs nginx with a configuration from this tree must find each host that
/// configuration proxies to among the services of its own stack. nginx resolves an upstream's host when it
/// starts and refuses to start when one is missing (<c>host not found in upstream "web:80"</c>), so a gateway
/// whose stack lacks one of its upstreams restarts in a loop and nothing is reachable through it. The hosts are
/// read from the mounted configuration (each <c>server</c> in an <c>upstream</c> block, and each
/// <c>proxy_pass</c> that names a host directly), never from a list, so a new upstream is checked as soon as it
/// is added. A host is provided by a service of that name, its <c>container_name</c> or one of its network
/// aliases, and that service must start whenever the proxy does: it may not sit behind a profile the proxy does
/// not share. Overlays are read as written: an overlay that runs a proxy must name the services it proxies to.
/// Includes a liveness self-test (the discovery must reach every shipped proxy) and detector unit tests.
/// </summary>
public sealed partial class ComposeNginxUpstreamTests
{
    public static TheoryData<string, string, string> ShippedNginxServices()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (compose, service, conf) in DiscoverNginxServices())
            data.Add(compose, service, conf);
        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedNginxServices))]
    public void ComposeNginx_ShouldFindEveryUpstreamHostInItsOwnStack(string compose, string service, string conf)
    {
        var violations = Scan(File.ReadAllText(RepoPath(compose)), service, File.ReadAllText(RepoPath(conf)));

        violations.Should().BeEmpty(
            $"'{service}' in {compose} mounts {conf}; nginx refuses to start when an upstream host does not " +
            "resolve, so the proxy restarts in a loop and nothing is reachable through it");
    }

    [Fact]
    public void ShippedNginxServices_ShouldIncludeEveryGatewayAndTheLoadBalancer_WhenDiscovered()
    {
        DiscoverNginxServices().Select(p => (p.Compose, p.Service)).Should().Contain(
            [
                ("docker/docker-compose.full.yml", "nginx-gateway"),
                ("docker/docker-compose.production.yml", "nginx-gateway"),
                ("docker/docker-compose.reference-smb.yml", "nginx-gateway"),
                ("docker/demo/docker-compose.demo.yml", "nginx-gateway"),
                ("docker/docker-compose.scale.yml", "nginx-lb"),
            ],
            "the discovery must reach every shipped nginx proxy; finding fewer means it broke");
    }

    [Fact]
    public void ShippedNginxServices_ShouldIncludeEveryServiceThatRunsAnNginxImage_WhenDiscovered()
    {
        var discovered = DiscoverNginxServices().Select(p => (p.Compose, p.Service)).ToHashSet();
        var nginxServices = ShippedComposeFiles()
            .SelectMany(compose => ForwardedHeadersTopologyTests.ServiceBlocks(File.ReadAllText(compose))
                .Where(s => NginxImageLine().IsMatch(s.Value))
                .Select(s => (ToRepoRelative(compose), s.Key)))
            .ToList();

        nginxServices.Should().NotBeEmpty("the shipped stacks run nginx proxies");
        nginxServices.Should().OnlyContain(s => discovered.Contains(s),
            "a service that runs nginx must mount its configuration as '- ./x.conf:/etc/nginx/...', the form the " +
            "upstream check reads; any other form would leave that proxy unchecked");
    }

    [Fact]
    public void UpstreamHosts_ShouldReadUpstreamServersAndDirectProxyPasses_WhenParsed()
    {
        const string config =
            "upstream api { server platform-api:5000 max_fails=0; keepalive 8; }\n" +
            "upstream web { server web:80; }\n" +
            "server {\n" +
            "  location /api/ { proxy_pass http://api; }\n" +
            "  location /x/ { proxy_pass http://realtime:5030/hubs/; }\n" +
            "  location /y/ { proxy_pass https://mail/; }\n" +
            "  location /v/ { proxy_pass http://$backend; }\n" +
            "  location /l/ { proxy_pass http://127.0.0.1:9000; }\n" +
            "  location / { proxy_pass http://web; }\n" +
            "}\n";

        UpstreamHosts(config).Should().BeEquivalentTo(["platform-api", "web", "realtime", "mail"]);
    }

    [Fact]
    public void Scan_ShouldReport_WhenAnUpstreamHostIsNotAServiceOfTheStack()
    {
        const string compose =
            "services:\n" +
            "  platform-api:\n" +
            "    image: api\n" +
            "  nginx-gateway:\n" +
            "    image: nginx\n";
        const string config = "upstream api { server platform-api:5000; }\nupstream web { server web:80; }\n";

        Scan(compose, "nginx-gateway", config).Should().ContainSingle()
            .Which.Should().Contain("'web'");
    }

    [Fact]
    public void Scan_ShouldAccept_WhenTheHostIsAContainerNameOrANetworkAlias()
    {
        const string compose =
            "services:\n" +
            "  api:\n" +
            "    image: api\n" +
            "    container_name: platform-api\n" +
            "  console:\n" +
            "    image: web\n" +
            "    networks:\n" +
            "      default:\n" +
            "        aliases:\n" +
            "          - web\n" +
            "  nginx-gateway:\n" +
            "    image: nginx\n";
        const string config = "upstream api { server platform-api:5000; }\nupstream web { server web:80; }\n";

        Scan(compose, "nginx-gateway", config).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldReport_WhenTheHostOnlyStartsUnderAProfileTheProxyDoesNotShare()
    {
        const string compose =
            "services:\n" +
            "  web:\n" +
            "    image: web\n" +
            "    profiles:\n" +
            "      - console\n" +
            "  nginx-gateway:\n" +
            "    image: nginx\n";
        const string config = "upstream web { server web:80; }\n";

        Scan(compose, "nginx-gateway", config).Should().ContainSingle()
            .Which.Should().Contain("profile");
    }

    [Fact]
    public void Scan_ShouldAccept_WhenTheProfileIsResetOrShared()
    {
        const string compose =
            "services:\n" +
            "  redis:\n" +
            "    profiles: !reset []\n" +
            "  web:\n" +
            "    image: web\n" +
            "    profiles: [console]\n" +
            "  nginx-gateway:\n" +
            "    image: nginx\n" +
            "    profiles: [\"console\"]\n";
        const string config = "upstream web { server web:80; }\nupstream r { server redis:6379; }\n";

        Scan(compose, "nginx-gateway", config).Should().BeEmpty();
    }

    /// <summary>One line per upstream host of <paramref name="config"/> that the stack does not start with the proxy.</summary>
    internal static List<string> Scan(string compose, string proxy, string config)
    {
        var services = ForwardedHeadersTopologyTests.ServiceBlocks(compose);
        var proxyProfiles = Profiles(services[proxy]);
        var violations = new List<string>();
        foreach (var host in UpstreamHosts(config))
        {
            var providers = services
                .Where(s => s.Key == host || ContainerName(s.Value) == host || Aliases(s.Value).Contains(host))
                .ToList();
            if (providers.Count == 0)
            {
                violations.Add($"upstream host '{host}' is not a service, container_name or network alias of this " +
                    $"stack (services: {string.Join(", ", services.Keys)})");
                continue;
            }

            if (!providers.Any(p => Profiles(p.Value) is var own && (own.Count == 0 || own.Overlaps(proxyProfiles))))
            {
                violations.Add($"upstream host '{host}' is provided only by a service behind a profile " +
                    $"'{proxy}' does not share, so the proxy starts without it");
            }
        }

        return violations;
    }

    /// <summary>The hosts <paramref name="config"/> proxies to: upstream servers and direct proxy_pass hosts.</summary>
    internal static List<string> UpstreamHosts(string config)
    {
        var directives = NginxAccessLogScanner.Descendants(NginxAccessLogScanner.Parse(config)).ToList();
        var upstreams = directives.Where(d => d.Name == "upstream" && d.Children is not null).ToList();
        var upstreamNames = upstreams.Select(u => u.Args[0]).ToHashSet(StringComparer.Ordinal);

        var hosts = upstreams
            .SelectMany(u => u.Children!)
            .Where(d => d.Name == "server" && d.Args.Count > 0)
            .Select(d => HostOf(d.Args[0]))
            .Concat(directives
                .Where(d => d.Name == "proxy_pass" && d.Args.Count == 1)
                .Select(d => ProxyPassHost().Match(d.Args[0]))
                .Where(m => m.Success && !upstreamNames.Contains(m.Groups["host"].Value))
                .Select(m => m.Groups["host"].Value));

        return hosts
            .Where(h => h.Length > 0 && !h.Contains('$', StringComparison.Ordinal) && !h.StartsWith("unix:", StringComparison.Ordinal)
                && h != "localhost" && !IpLiteral().IsMatch(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string HostOf(string address)
    {
        var colon = address.LastIndexOf(':');
        return colon > 0 && !address.StartsWith("unix:", StringComparison.Ordinal) ? address[..colon] : address;
    }

    /// <summary>Each (compose file, service, configuration) whose service bind-mounts an nginx configuration from this tree.</summary>
    private static List<(string Compose, string Service, string Conf)> DiscoverNginxServices()
    {
        var found = new List<(string, string, string)>();
        foreach (var compose in ShippedComposeFiles())
        {
            foreach (var (service, block) in ForwardedHeadersTopologyTests.ServiceBlocks(File.ReadAllText(compose)))
            {
                foreach (Match mount in NginxConfMount().Matches(block))
                {
                    var source = Path.GetFullPath(Path.Join(Path.GetDirectoryName(compose), mount.Groups["source"].Value));
                    if (File.Exists(source))
                        found.Add((ToRepoRelative(compose), service, ToRepoRelative(source)));
                }
            }
        }

        return found.Order().ToList();
    }

    private static IEnumerable<string> ShippedComposeFiles() =>
        Directory.EnumerateFiles(RepoPath(""), "docker-compose*.y*ml")
            .Concat(Directory.EnumerateFiles(RepoPath("docker"), "docker-compose*.y*ml", SearchOption.AllDirectories));

    private static string? ContainerName(string serviceBlock)
    {
        var match = ContainerNameLine().Match(serviceBlock);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static HashSet<string> Aliases(string serviceBlock)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in AliasesBlock().Matches(serviceBlock))
        {
            foreach (Capture item in block.Groups["item"].Captures)
                aliases.Add(item.Value);
        }

        return aliases;
    }

    private static HashSet<string> Profiles(string serviceBlock)
    {
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        var inline = ProfilesInline().Match(serviceBlock);
        if (inline.Success)
        {
            foreach (var item in inline.Groups["items"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                profiles.Add(item.Trim('"', '\''));
            return profiles;
        }

        var listed = ProfilesList().Match(serviceBlock);
        if (listed.Success)
        {
            foreach (Capture item in listed.Groups["item"].Captures)
                profiles.Add(item.Value);
        }

        return profiles;
    }

    [GeneratedRegex("""^\s*-\s*["']?(?<source>[^\s:"']+\.conf):/etc/nginx/""", RegexOptions.Multiline)]
    private static partial Regex NginxConfMount();

    [GeneratedRegex("""^    image:\s*["']?(?:[^\s"'#]*/)?nginx[^\s"'#/:@]*(?:[:@"'\s]|$)""", RegexOptions.Multiline)]
    private static partial Regex NginxImageLine();

    [GeneratedRegex(@"^(?:https?)://(?<host>[^/:]+)")]
    private static partial Regex ProxyPassHost();

    [GeneratedRegex(@"^(?:\d{1,3}\.){3}\d{1,3}$|^\[")]
    private static partial Regex IpLiteral();

    [GeneratedRegex("""^    container_name:\s*["']?(?<value>[^\s"'#]+)""", RegexOptions.Multiline)]
    private static partial Regex ContainerNameLine();

    [GeneratedRegex("""^(?<indent>[ ]+)aliases:[ ]*\n(?:\k<indent>[ ]+-[ ]*["']?(?<item>[^\s"'#]+)["']?\s*\n?)+""", RegexOptions.Multiline)]
    private static partial Regex AliasesBlock();

    [GeneratedRegex(@"^    profiles:\s*(?:!reset\s*)?\[(?<items>[^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex ProfilesInline();

    [GeneratedRegex("""^    profiles:\s*\n(?:      -\s*["']?(?<item>[^\s"'#]+)["']?\s*\n?)+""", RegexOptions.Multiline)]
    private static partial Regex ProfilesList();

    private static string RepoPath(string relativePath) =>
        Path.Join(Directory.GetParent(TestTreeSource.TestsRoot())!.FullName, relativePath);

    private static string ToRepoRelative(string path) =>
        Path.GetRelativePath(RepoPath(""), path).Replace(Path.DirectorySeparatorChar, '/');
}
