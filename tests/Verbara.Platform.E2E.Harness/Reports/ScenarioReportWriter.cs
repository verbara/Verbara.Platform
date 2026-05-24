using System.Text;
using System.Text.Json;

namespace Verbara.Platform.E2E.Harness.Reports;

/// <summary>
/// Persists a <see cref="ScenarioReport"/> as JSON (machine) + Markdown
/// (human + auditor friendly) under <c>HARNESS_REPORT_DIR/&lt;timestamp&gt;/</c>.
/// </summary>
internal static class ScenarioReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static (string JsonPath, string MarkdownPath) Write(ScenarioReport report, string baseDirectory)
    {
        var stamp = report.StartedAt.ToString("yyyyMMdd-HHmmss");
        var runDir = Path.Combine(baseDirectory, stamp);
        Directory.CreateDirectory(runDir);

        var jsonPath = Path.Combine(runDir, $"{report.ScenarioName}.json");
        var mdPath = Path.Combine(runDir, $"{report.ScenarioName}.md");

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllText(mdPath, RenderMarkdown(report));

        return (jsonPath, mdPath);
    }

    public static string RenderMarkdown(ScenarioReport r)
    {
        var verdict = r.Passed ? "✅ PASS" : "❌ FAIL";
        var sb = new StringBuilder();
        sb.AppendLine($"# Harness scenario — {r.ScenarioName} — {verdict}");
        sb.AppendLine();
        sb.AppendLine($"- **Topology:** {r.Topology}");
        sb.AppendLine($"- **Started:** {r.StartedAt:O}");
        sb.AppendLine($"- **Completed:** {r.CompletedAt:O}");
        sb.AppendLine($"- **Duration:** {(r.CompletedAt - r.StartedAt).TotalSeconds:F2} s");
        sb.AppendLine($"- **Pods observed:** {r.PodCount}");
        sb.AppendLine($"- **SignalR clients:** {r.ClientCount}");
        sb.AppendLine($"- **Events emitted:** {r.EventsEmitted}");
        sb.AppendLine($"- **Expected receives per client:** {r.ExpectedReceivesPerClient}");
        sb.AppendLine();
        sb.AppendLine("## Receives per client (each must equal expected)");
        sb.AppendLine();
        sb.AppendLine("| Client | Received |");
        sb.AppendLine("|---:|---:|");
        for (var i = 0; i < r.ActualReceivesPerClient.Count; i++)
        {
            sb.AppendLine($"| {i} | {r.ActualReceivesPerClient[i]} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Per-pod outcome rollup (audit endpoint)");
        sb.AppendLine();
        sb.AppendLine("| Pod | Forwarded | SkippedNotLeader | Audit URL |");
        sb.AppendLine("|---|---:|---:|---|");
        var podsInOrder = r.PerPodForwarded.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        for (var i = 0; i < podsInOrder.Count; i++)
        {
            var pod = podsInOrder[i];
            var f = r.PerPodForwarded.TryGetValue(pod, out var fv) ? fv : 0;
            var s = r.PerPodSkippedNotLeader.TryGetValue(pod, out var sv) ? sv : 0;
            var url = i < r.AuditBaseUrls.Count ? r.AuditBaseUrls[i] : "—";
            sb.AppendLine($"| {pod} | {f} | {s} | {url} |");
        }
        sb.AppendLine();
        sb.AppendLine($"- **Total Forwarded:** {r.TotalForwarded}");
        sb.AppendLine($"- **Total SkippedNotLeader:** {r.TotalSkippedNotLeader}");
        sb.AppendLine($"- **Leader pod(s):** {(r.LeaderPodInstanceIds.Count == 0 ? "(none)" : string.Join(", ", r.LeaderPodInstanceIds))}");
        sb.AppendLine();

        if (r.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var w in r.Warnings)
            {
                sb.AppendLine($"- ⚠️ {w}");
            }
            sb.AppendLine();
        }

        if (r.Failures.Count > 0)
        {
            sb.AppendLine("## Failures");
            sb.AppendLine();
            foreach (var f in r.Failures)
            {
                sb.AppendLine($"- ❌ {f}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
