using System.Text.RegularExpressions;

namespace Verbara.Platform.Api.Tests.Voice;

/// <summary>
/// stasis-app-has-one-registrant (spec <c>voice-stasis-app-ownership</c>, "Shipped deployment files name a distinct
/// node app", Platform/ADR-0044): every deployment file the repo ships that pins the dialplan's Stasis app for the Api
/// also pins the node client's app, to a different name, so a reader never "fixes" the two back into one.
/// </summary>
public sealed partial class ShippedStasisAppNamesTests
{
    private static readonly string[] ComposeFiles =
    [
        "docker/docker-compose.full.yml",
        "docker/docker-compose.reference-smb.yml",
        "docker/demo/docker-compose.demo.yml",
    ];

    private const string HelmValues = "infra/k8s/helm/platform/values.yaml";
    private const string HelmApiDeployment = "infra/k8s/helm/platform/templates/platform-api-deployment.yaml";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Verbara.Platform.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string[] Lines(string relative) =>
        File.ReadAllLines(Path.Join(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    [GeneratedRegex(@"^  (?<name>[A-Za-z0-9_.-]+):\s*$")]
    private static partial Regex ComposeService();

    [GeneratedRegex(@"^\s+(?<key>Asterisk__Ari__(?:Node)?Application):\s*(?<value>\S+)\s*$")]
    private static partial Regex ComposeAppVariable();

    [GeneratedRegex(@"^(?<indent>\s*)ari:\s*$")]
    private static partial Regex HelmAriBlock();

    [GeneratedRegex(@"^\s+(?<key>application|nodeApplication):\s*(?<value>\S+)\s*$")]
    private static partial Regex HelmAppKey();

    /// <summary>Per compose service: the dialplan app and the node app it sets (null when unset).</summary>
    private static Dictionary<string, (string? App, string? Node)> ComposeServices(string relative)
    {
        var result = new Dictionary<string, (string? App, string? Node)>(StringComparer.Ordinal);
        string? service = null;
        foreach (var line in Lines(relative))
        {
            if (ComposeService().Match(line) is { Success: true } s)
            {
                service = s.Groups["name"].Value;
                continue;
            }

            if (service is null || ComposeAppVariable().Match(line) is not { Success: true } m)
                continue;

            var current = result.GetValueOrDefault(service);
            var value = m.Groups["value"].Value.Trim('"', '\'');
            result[service] = m.Groups["key"].Value == "Asterisk__Ari__Application"
                ? current with { App = value }
                : current with { Node = value };
        }

        return result;
    }

    /// <summary>The chart's <c>api.asterisk.ari.application</c> and <c>nodeApplication</c> values.</summary>
    private static (string? App, string? Node) HelmAriValues()
    {
        (string? App, string? Node) result = default;
        int? ariIndent = null;
        foreach (var line in Lines(HelmValues))
        {
            if (HelmAriBlock().Match(line) is { Success: true } block)
            {
                ariIndent = block.Groups["indent"].Value.Length;
                continue;
            }

            if (ariIndent is null || line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
                continue;

            var indent = line.Length - line.TrimStart().Length;
            if (indent <= ariIndent)
            {
                ariIndent = null;
                continue;
            }

            if (HelmAppKey().Match(line) is { Success: true } m)
            {
                var value = m.Groups["value"].Value.Trim('"', '\'');
                result = m.Groups["key"].Value == "application" ? result with { App = value } : result with { Node = value };
            }
        }

        return result;
    }

    [Fact]
    public void ShippedDeploymentFiles_ShouldSetADistinctNodeApplication_WhereverTheyPinTheApplication()
    {
        var problems = new List<string>();

        foreach (var file in ComposeFiles)
        {
            var pinned = ComposeServices(file).Where(s => s.Value.App is not null).ToList();
            if (pinned.Count == 0)
                problems.Add($"{file}: no service sets Asterisk__Ari__Application (the test's premise moved)");

            foreach (var (service, (app, node)) in pinned)
            {
                if (node is null)
                    problems.Add($"{file}: service '{service}' sets Asterisk__Ari__Application={app} but no Asterisk__Ari__NodeApplication");
                else if (string.Equals(app, node, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{file}: service '{service}' sets Asterisk__Ari__NodeApplication equal to Asterisk__Ari__Application ({app})");
            }
        }

        var (helmApp, helmNode) = HelmAriValues();
        if (helmApp is null)
            problems.Add($"{HelmValues}: api.asterisk.ari.application not found (the test's premise moved)");
        else if (helmNode is null)
            problems.Add($"{HelmValues}: sets ari.application={helmApp} but no ari.nodeApplication");
        else if (string.Equals(helmApp, helmNode, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{HelmValues}: ari.nodeApplication equals ari.application ({helmApp})");

        var template = string.Join('\n', Lines(HelmApiDeployment));
        if (template.Contains("name: Asterisk__Ari__Application", StringComparison.Ordinal)
            && !(template.Contains("name: Asterisk__Ari__NodeApplication", StringComparison.Ordinal)
                 && template.Contains(".Values.api.asterisk.ari.nodeApplication", StringComparison.Ordinal)))
            problems.Add($"{HelmApiDeployment}: injects Asterisk__Ari__Application but not Asterisk__Ari__NodeApplication from .Values.api.asterisk.ari.nodeApplication");

        problems.Should().BeEmpty("every shipped file that pins the dialplan app must pin a distinct node app:\n{0}",
            string.Join("\n", problems));
    }
}
