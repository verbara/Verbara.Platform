using System.Text;
using System.Text.Json;
using Verbara.Platform.E2E.Harness.Scenarios;

namespace Verbara.Platform.E2E.Harness.Reports;

/// <summary>
/// Persists a <see cref="ClusterFramesReport"/> as JSON (the record, every REST step and node read, the bridge metric
/// lines and the raw audit pages) plus a Markdown summary under <c>HARNESS_REPORT_DIR/&lt;timestamp&gt;/</c>.
/// </summary>
internal static class ClusterFramesReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static (string JsonPath, string MarkdownPath) Write(ClusterFramesReport report, string baseDirectory)
    {
        var runDir = Path.Combine(baseDirectory, report.StartedAt.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(runDir);
        var jsonPath = Path.Combine(runDir, $"{report.ScenarioName}.json");
        var mdPath = Path.Combine(runDir, $"{report.ScenarioName}.md");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllText(mdPath, RenderMarkdown(report));
        return (jsonPath, mdPath);
    }

    public static string RenderMarkdown(ClusterFramesReport r)
    {
        static string V(bool b) => b ? "PASS" : "FAIL";
        var v = r.Verdict;
        var k = r.Record;
        var sb = new StringBuilder();
        sb.AppendLine($"# Harness scenario — {r.ScenarioName} — {(r.FixtureError is null ? V(v.Passed) : "FIXTURE ERROR")}");
        sb.AppendLine();
        if (r.FixtureError is not null)
        {
            sb.AppendLine($"- **Fixture error (rerun, never count):** {r.FixtureError}");
        }
        sb.AppendLine($"- **Tenant:** {r.Tenant} (agent); host client and REST as the Admin of {r.AdminTenant}; liveness client as the Admin of {r.LivenessAdminTenant}");
        sb.AppendLine($"- **Liveness window:** {r.LivenessWindowStart:O} → {r.LivenessWindowEnd:O}; cluster window {r.ClusterWindowStart:O} → {r.ClusterWindowEnd:O}");
        sb.AppendLine($"- **1. Liveness:** {V(v.LivenessPassed)} (K = {k.ExpectedAgentFrames}; liveness client frames {k.AgentFramesInLiveness}; Forwarded {ClusterFramesScenario.CoreAgentEventType} rows of the tenant {k.AgentForwardedRowsInLiveness}, all tenants {r.AgentForwardedRowsInLivenessAllTenants}; client frames whole run {r.AgentFramesWholeRun})");
        sb.AppendLine($"- **2. No cluster frame:** {V(v.NoClusterFramesPassed)} (host client {k.ClusterFrames.Count}, liveness client {k.LivenessClientClusterFrames.Count} OnClusterNodeStateChanged frame(s))");
        sb.AppendLine($"- **3. No cluster audit row:** {V(v.NoClusterAuditRowsPassed)} ({k.ClusterAuditRows} row(s) of {ClusterFramesScenario.ClusterEventType}, any outcome)");
        sb.AppendLine($"- **4. REST steps:** {V(v.RestStepsPassed)} (fixture read {Nodes(k.FixtureRead)}; pre-drain read {Nodes(k.PreDrainRead)}; drain poll saw draining {k.DrainPollSawDraining}; left draining after cancel {k.CancelPollLeftDraining})");
        sb.AppendLine($"- **5. Host client connected at the end:** {V(v.HostClientConnectedPassed)} ({k.HostClientStateAtEnd})");
        sb.AppendLine();
        sb.AppendLine("## REST steps");
        sb.AppendLine();
        sb.AppendLine("| Step | Request | Status | Expected | OK |");
        sb.AppendLine("|---|---|---:|---|---|");
        foreach (var s in k.Steps)
        {
            sb.AppendLine($"| {s.Name} | {s.Method} {s.Path} | {s.Status} | {s.Expected} | {s.Ok} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Node reads");
        sb.AppendLine();
        foreach (var n in r.NodeReads)
        {
            sb.AppendLine($"- {n.Label} at {n.At:O}: {Nodes(n)}");
        }
        sb.AppendLine();
        sb.AppendLine("## Cluster audit rows and bridge metrics");
        sb.AppendLine();
        foreach (var (key, rows) in r.ClusterAuditRowsByEventTypeAndOutcome)
        {
            sb.AppendLine($"- audit {key}: {rows}");
        }
        foreach (var m in r.BridgeMetricsBeforeClusterWindow)
        {
            sb.AppendLine($"- before: `{m}`");
        }
        foreach (var m in r.BridgeMetricsAfterClusterWindow)
        {
            sb.AppendLine($"- after: `{m}`");
        }
        foreach (var f in k.ClusterFrames)
        {
            sb.AppendLine($"- host client cluster frame at {f.ReceivedAt:O}: `{f.PayloadJson}`");
        }
        foreach (var f in k.LivenessClientClusterFrames)
        {
            sb.AppendLine($"- liveness client cluster frame at {f.ReceivedAt:O}: `{f.PayloadJson}`");
        }
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var w in r.Warnings)
            {
                sb.AppendLine($"- {w}");
            }
        }
        if (v.Failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Failures");
            sb.AppendLine();
            foreach (var f in v.Failures)
            {
                sb.AppendLine($"- {f}");
            }
        }
        return sb.ToString();
    }

    private static string Nodes(NodeRead? n) =>
        n is null ? "none" : $"HTTP {n.Status} [{string.Join(", ", n.States.Select(kv => $"{kv.Key}={kv.Value}"))}]";
}
