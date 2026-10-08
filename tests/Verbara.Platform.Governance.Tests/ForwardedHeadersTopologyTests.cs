using System.Text.RegularExpressions;

namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// Every shipped topology that puts a proxy in front of the API must make the API trust that proxy's
/// <c>X-Forwarded-For</c>, or the API sees the proxy's address for every visitor and the per-client limits
/// (the anonymous WebChat endpoints) become one bucket shared by all of them. Scans the compose files (each
/// nginx proxy that mounts a shipped configuration has a fixed address, and the same file's
/// <c>platform-api</c> trusts exactly that address), the nginx configurations (each location that proxies to
/// the API sends the client address) and the Helm chart (the API deployment renders the trusted networks
/// from a value with a non-empty default).
/// </summary>
public sealed partial class ForwardedHeadersTopologyTests
{
    private const string TrustedProxiesVariable = "ForwardedHeaders__TrustedProxies__";

    private static readonly string[] ApiProxyConfs =
    [
        "docker/nginx-gateway.conf",
        "docker/nginx-loadbalancer.conf",
    ];

    private const string HelmValues = "infra/k8s/helm/platform/values.yaml";
    private const string HelmApiDeployment = "infra/k8s/helm/platform/templates/platform-api-deployment.yaml";

    public static TheoryData<string, string> ShippedProxies()
    {
        var data = new TheoryData<string, string>();
        foreach (var (compose, service) in DiscoverProxies())
            data.Add(compose, service);
        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedProxies))]
    public void ComposeProxy_ShouldHaveAFixedAddressThePlatformApiTrusts(string compose, string service)
    {
        var services = ServiceBlocks(ReadRepoFile(compose));
        var address = FixedAddress(services[service]);

        address.Should().NotBeNull(
            $"'{service}' in {compose} must have a fixed ipv4_address, so the API can trust exactly that address");

        services.Should().ContainKey("platform-api", $"{compose} runs '{service}' in front of the API");
        TrustedProxies(services["platform-api"]).Should().Contain($"{address}/32",
            $"platform-api in {compose} must trust '{service}' (ForwardedHeaders:TrustedProxies), or every " +
            "visitor shares the proxy's address and one per-client limit");
    }

    [Fact]
    public void ShippedProxies_ShouldIncludeEveryGatewayAndTheLoadBalancer_WhenDiscovered()
    {
        DiscoverProxies().Should().Contain(
            [
                ("docker/docker-compose.full.yml", "nginx-gateway"),
                ("docker/docker-compose.production.yml", "nginx-gateway"),
                ("docker/docker-compose.reference-smb.yml", "nginx-gateway"),
                ("docker/demo/docker-compose.demo.yml", "nginx-gateway"),
                ("docker/docker-compose.scale.yml", "nginx-lb"),
            ],
            "the discovery must reach every shipped proxy in front of the API; finding fewer means it broke");
    }

    [Theory]
    [MemberData(nameof(ApiProxyConfNames))]
    public void NginxConf_ShouldSendTheClientAddressInEveryLocationThatProxiesToTheApi(string conf)
    {
        var directives = NginxAccessLogScanner.Parse(ReadRepoFile(conf));
        var apiUpstreams = NginxAccessLogScanner.Descendants(directives)
            .Where(d => d.Name == "upstream" && d.Children is not null
                && d.Children.Any(s => s.Name == "server" && s.Args.Count > 0
                    && s.Args[0].StartsWith("platform-api:", StringComparison.Ordinal)))
            .Select(d => d.Args[0])
            .ToHashSet(StringComparer.Ordinal);
        var apiLocations = NginxAccessLogScanner.Descendants(directives)
            .Where(d => d.Name == "location" && d.Children is not null
                && d.Children.Any(p => p.Name == "proxy_pass" && p.Args.Count == 1
                    && apiUpstreams.Contains(p.Args[0].Replace("http://", "", StringComparison.Ordinal).TrimEnd('/'))))
            .ToList();

        apiLocations.Should().NotBeEmpty($"{conf} must proxy to the API; finding no location means the scan broke");
        apiLocations.Should().OnlyContain(
            location => location.Children!.Any(h => h.Name == "proxy_set_header" && h.Args.Count == 2
                && h.Args[0].Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase)
                && (h.Args[1] == "$proxy_add_x_forwarded_for" || h.Args[1] == "$remote_addr")),
            "the API reads the client address from the last X-Forwarded-For entry its trusted proxy appends");
    }

    public static TheoryData<string> ApiProxyConfNames() => new(ApiProxyConfs);

    [Fact]
    public void HelmChart_ShouldRenderTrustedProxiesFromAValueWithADefault()
    {
        var values = ReadRepoFile(HelmValues);
        var template = ReadRepoFile(HelmApiDeployment);

        HelmTrustedProxiesDefault().IsMatch(values).Should().BeTrue(
            "api.forwardedHeaders.trustedProxies must default to the network the cluster's gateway connects from");
        template.Should().Contain("(.Values.api.forwardedHeaders | default dict).trustedProxies")
            .And.Contain(TrustedProxiesVariable,
                "the API deployment must turn the value into ForwardedHeaders:TrustedProxies entries");
    }

    [Fact]
    public void ServiceBlocks_ShouldSplitTheServicesAndResolveDefaults_WhenParsed()
    {
        const string compose =
            "services:\n" +
            "  platform-api:\n" +
            "    environment:\n" +
            "      ForwardedHeaders__TrustedProxies__0: \"${GATEWAY_IP:-172.31.250.10}/32\"\n" +
            "      - ForwardedHeaders__TrustedProxies__1=10.0.0.5/32\n" +
            "  nginx-gateway:\n" +
            "    networks:\n" +
            "      default:\n" +
            "        ipv4_address: ${GATEWAY_IP:-172.31.250.10}\n" +
            "networks:\n" +
            "  default:\n" +
            "    ipv4_address: 9.9.9.9\n";

        var services = ServiceBlocks(compose);

        services.Keys.Should().Equal("platform-api", "nginx-gateway");
        TrustedProxies(services["platform-api"]).Should().Equal("172.31.250.10/32", "10.0.0.5/32");
        FixedAddress(services["nginx-gateway"]).Should().Be("172.31.250.10");
    }

    /// <summary>Each (compose file, service) whose service bind-mounts a shipped API proxy configuration.</summary>
    private static List<(string Compose, string Service)> DiscoverProxies()
    {
        var composeFiles = Directory.EnumerateFiles(RepoPath(""), "docker-compose*.y*ml")
            .Concat(Directory.EnumerateFiles(RepoPath("docker"), "docker-compose*.y*ml", SearchOption.AllDirectories));

        var proxies = new List<(string, string)>();
        foreach (var compose in composeFiles)
        {
            foreach (var (service, block) in ServiceBlocks(File.ReadAllText(compose)))
            {
                var mountsApiProxy = NginxBindMount().Matches(block)
                    .Select(m => ToRepoRelative(Path.GetFullPath(Path.Join(Path.GetDirectoryName(compose), m.Groups["source"].Value))))
                    .Any(ApiProxyConfs.Contains);
                if (mountsApiProxy)
                    proxies.Add((ToRepoRelative(compose), service));
            }
        }

        return proxies.Order().ToList();
    }

    /// <summary>The text of each service under the top-level <c>services:</c> key, by service name.</summary>
    private static Dictionary<string, string> ServiceBlocks(string compose)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var inServices = false;
        string? current = null;
        var text = new System.Text.StringBuilder();

        void Flush()
        {
            if (current is not null)
                blocks[current] = text.ToString();
            current = null;
            text.Clear();
        }

        foreach (var line in compose.Split('\n'))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#')
            {
                Flush();
                inServices = line.TrimEnd() == "services:";
                continue;
            }

            if (!inServices)
                continue;

            var service = ServiceKey().Match(line);
            if (service.Success)
            {
                Flush();
                current = service.Groups["name"].Value;
                continue;
            }

            if (current is not null)
                text.Append(line).Append('\n');
        }

        Flush();
        return blocks;
    }

    private static string? FixedAddress(string serviceBlock)
    {
        var match = Ipv4Address().Match(serviceBlock);
        return match.Success ? ResolveDefaults(match.Groups["value"].Value) : null;
    }

    private static List<string> TrustedProxies(string serviceBlock) =>
        TrustedProxyEntry().Matches(serviceBlock).Select(m => ResolveDefaults(m.Groups["value"].Value)).ToList();

    /// <summary>Replaces each <c>${NAME:-default}</c> with its default, as compose does when NAME is unset.</summary>
    private static string ResolveDefaults(string value) => DefaultedVariable().Replace(value, m => m.Groups["default"].Value);

    [GeneratedRegex(@"^  (?<name>[A-Za-z0-9._-]+):\s*$")]
    private static partial Regex ServiceKey();

    [GeneratedRegex("""^\s*ipv4_address:\s*["']?(?<value>[^\s"'#]+)""", RegexOptions.Multiline)]
    private static partial Regex Ipv4Address();

    [GeneratedRegex("""ForwardedHeaders__TrustedProxies__\d+["']?\s*[:=]\s*["']?(?<value>[^\s"'#]+)""")]
    private static partial Regex TrustedProxyEntry();

    [GeneratedRegex(@"\$\{[A-Za-z_][A-Za-z0-9_]*:-(?<default>[^}]*)\}")]
    private static partial Regex DefaultedVariable();

    [GeneratedRegex("""^\s*-\s*["']?(?<source>[^\s:"']+):/etc/nginx/""", RegexOptions.Multiline)]
    private static partial Regex NginxBindMount();

    // api:\n ... forwardedHeaders:\n   trustedProxies:\n     - "10.244.0.0/16"
    [GeneratedRegex("""^  forwardedHeaders:\s*\n(?:\s*#.*\n)*    trustedProxies:\s*\n(?:\s*#.*\n)*      - ["']?[0-9a-fA-F.:]+/\d+""", RegexOptions.Multiline)]
    private static partial Regex HelmTrustedProxiesDefault();

    private static string ReadRepoFile(string relativePath)
    {
        var path = RepoPath(relativePath);
        File.Exists(path).Should().BeTrue($"the file must exist at {path}");
        return File.ReadAllText(path);
    }

    private static string RepoPath(string relativePath) =>
        Path.Join(Directory.GetParent(TestTreeSource.TestsRoot())!.FullName, relativePath);

    private static string ToRepoRelative(string path) =>
        Path.GetRelativePath(RepoPath(""), path).Replace(Path.DirectorySeparatorChar, '/');
}
