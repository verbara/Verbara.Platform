using System.Text;
using System.Text.Json;
using Verbara.Platform.E2E.Harness.Scenarios;

namespace Verbara.Platform.E2E.Harness.Reports;

/// <summary>
/// Persists a <see cref="ConversationFramesReport"/> as JSON (every frame of every client, the workload's output
/// and the raw audit pages) plus a Markdown summary under <c>HARNESS_REPORT_DIR/&lt;timestamp&gt;/</c>.
/// </summary>
internal static class ConversationFramesReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static (string JsonPath, string MarkdownPath) Write(ConversationFramesReport report, string baseDirectory)
    {
        var runDir = Path.Combine(baseDirectory, report.StartedAt.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(runDir);
        var jsonPath = Path.Combine(runDir, $"{report.ScenarioName}.json");
        var mdPath = Path.Combine(runDir, $"{report.ScenarioName}.md");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllText(mdPath, RenderMarkdown(report));
        return (jsonPath, mdPath);
    }

    public static string RenderMarkdown(ConversationFramesReport r)
    {
        static string V(bool b) => b ? "PASS" : "FAIL";
        var sb = new StringBuilder();
        sb.AppendLine($"# Harness scenario — {r.ScenarioName} — {V(r.Passed)}");
        sb.AppendLine();
        sb.AppendLine($"- **Window:** {r.WindowStart:O} → workload end {r.WorkloadEnd:O}, completed {r.CompletedAt:O}");
        sb.AppendLine($"- **Tenant:** {r.Tenant}; other tenant (negative control): {r.OtherTenant}");
        sb.AppendLine($"- **Workload:** {r.WebChatConversations} WebChat conversation(s) {string.Join(", ", r.WebChatConversationIds)}; {r.VoiceCalls} voice call(s) {string.Join(", ", r.VoiceConversationIds)}");
        sb.AppendLine($"- **Declared frames per client:** {r.DeclaredFramesPerClient}");
        sb.AppendLine($"- **1. Declared frames:** {V(r.DeclaredFramesPassed)}");
        sb.AppendLine($"- **2. Exactly once:** {V(r.ExactlyOncePassed)} (client totals {string.Join(", ", r.Clients.Select(c => c.TotalFrames))}; conversation.state_changed Forwarded rows of the tenant {r.CoreConversationForwardedForTenant})");
        sb.AppendLine($"- **3. Negative control:** {V(r.NegativeControlPassed)} (other-tenant frames {r.OtherTenantClient?.TotalFrames}; its own declared-frames verdict {(r.OtherTenantClient is { DeclaredFramesPassed: true } ? "PASS" : "FAIL")})");
        sb.AppendLine($"- **4. No Pro forward:** {V(r.NoProForwardPassed)} (conversation.state.changed Forwarded rows {r.ProConversationForwarded})");
        sb.AppendLine();
        sb.AppendLine("## Frames per client (arrival order)");
        foreach (var c in r.Clients.Append(r.OtherTenantClient).OfType<ClientRecord>())
        {
            sb.AppendLine();
            sb.AppendLine($"### {c.Client} — {c.TotalFrames} frame(s), declared {V(c.DeclaredFramesPassed)}, agent frames in window {c.AgentFramesInWindow}");
            sb.AppendLine();
            foreach (var conv in c.Conversations)
            {
                sb.AppendLine($"- {conv.Kind} `{conv.ConversationId}` {V(conv.Passed)}: [{string.Join(", ", conv.Actual)}]{(conv.Undeclared.Count > 0 ? $" (undeclared later: {string.Join(", ", conv.Undeclared)})" : string.Empty)}");
            }
            foreach (var f in c.FramesOnOtherConversations)
            {
                sb.AppendLine($"- other `{f.ConversationId}`: \"{f.PreviousState}\"->\"{f.NewState}\" at {f.ReceivedAt:O}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Audit outcomes by event type");
        sb.AppendLine();
        sb.AppendLine("| Event type | Outcome | Rows |");
        sb.AppendLine("|---|---|---:|");
        foreach (var (key, rows) in r.OutcomesByEventType)
        {
            var parts = key.Split('|');
            sb.AppendLine($"| {parts[0]} | {(parts.Length > 1 ? parts[1] : string.Empty)} | {rows} |");
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
        if (r.Failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Failures");
            sb.AppendLine();
            foreach (var f in r.Failures)
            {
                sb.AppendLine($"- {f}");
            }
        }
        return sb.ToString();
    }
}
