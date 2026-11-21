using Verbara.Platform.E2E.Harness.Config;
using Verbara.Platform.E2E.Harness.Reports;
using Verbara.Platform.E2E.Harness.Scenarios;

// Entrypoint. HARNESS_SCENARIO picks the scenario: "exactly-once" (default) or
// "conversation-frames" (platform-drops-the-pro-agent-bridge, tasks 1.1 and 4.1), or
// "conversation-frames-selfcheck" (that scenario's evaluator against synthetic records, offline), or
// "cluster-frames" (platform-drops-the-cluster-bridge, tasks 1.1 and 4.1) and "cluster-frames-selfcheck" (offline).
// Wrapper script (scripts/run-harness-talos.sh) populates the HARNESS_* env
// vars after preparing kubectl port-forwards for each Realtime pod.
//
// Exit codes:
//   0 = PASS
//   1 = FAIL (scenario invariant violation — see report Failures section)
//   2 = ERROR (env missing, login/network failure, etc.)
//
// Reports are written to ${HARNESS_REPORT_DIR}/<timestamp>/exactly-once.{json,md}
// (default: ./harness-reports/<timestamp>/).

// Offline negative controls of the conversation-frames evaluator; needs no lab and no other HARNESS_* variable.
if (string.Equals(Environment.GetEnvironmentVariable("HARNESS_SCENARIO"), ConversationFramesSelfCheck.Name, StringComparison.Ordinal))
{
    var selfCheckFailures = ConversationFramesSelfCheck.Run();
    foreach (var failure in selfCheckFailures)
    {
        Console.Error.WriteLine($"SELF-CHECK FAIL: {failure}");
    }

    Console.WriteLine(selfCheckFailures.Count == 0 ? "SELF-CHECK PASS" : $"SELF-CHECK FAIL ({selfCheckFailures.Count})");
    return selfCheckFailures.Count == 0 ? 0 : 1;
}

// Offline negative controls of the cluster-frames evaluator; needs no lab and no other HARNESS_* variable.
if (string.Equals(Environment.GetEnvironmentVariable("HARNESS_SCENARIO"), ClusterFramesSelfCheck.Name, StringComparison.Ordinal))
{
    var clusterSelfCheckFailures = ClusterFramesSelfCheck.Run();
    foreach (var failure in clusterSelfCheckFailures)
    {
        Console.Error.WriteLine($"SELF-CHECK FAIL: {failure}");
    }

    Console.WriteLine(clusterSelfCheckFailures.Count == 0 ? "SELF-CHECK PASS" : $"SELF-CHECK FAIL ({clusterSelfCheckFailures.Count})");
    return clusterSelfCheckFailures.Count == 0 ? 0 : 1;
}

try
{
    var config = HarnessConfig.FromEnvironment();
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Verbara.Platform.E2E.Harness — scenario '{config.Scenario}'.");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Api:      {config.ApiBaseUrl}");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Hub:      {config.RealtimeHubUrl}");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Tenant:   {config.Tenant}");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Pods:     {config.AuditBaseUrls.Count} ({string.Join(", ", config.AuditBaseUrls)})");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Clients:  {config.ClientCount}");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Events:   {config.EventCount}");
    Console.WriteLine();

    if (string.Equals(config.Scenario, ConversationFramesScenario.Name, StringComparison.Ordinal))
    {
        // platform-drops-the-pro-agent-bridge, tasks 1.1 and 4.1: the conversation window of the lab cell.
        using var conversationCts = new CancellationTokenSource(config.WorkloadTimeout + TimeSpan.FromMinutes(5));
        var (conversationReport, error) = await ConversationFramesScenario.RunAsync(config, conversationCts.Token);
        if (conversationReport is null)
        {
            Console.Error.WriteLine($"WORKLOAD ERROR: {error}");
            return 2;
        }

        var (cJson, cMd) = ConversationFramesReportWriter.Write(conversationReport, config.ReportDirectory);
        Console.WriteLine();
        Console.WriteLine(ConversationFramesReportWriter.RenderMarkdown(conversationReport));
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Report written to:");
        Console.WriteLine($"  - {cJson}");
        Console.WriteLine($"  - {cMd}");
        return conversationReport.Passed ? 0 : 1;
    }

    if (string.Equals(config.Scenario, ClusterFramesScenario.Name, StringComparison.Ordinal))
    {
        // platform-drops-the-cluster-bridge, tasks 1.1 and 4.1: the cluster cell. A fixture error exits 2 (rerun).
        using var clusterCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var clusterReport = await ClusterFramesScenario.RunAsync(config, clusterCts.Token);
        var (kJson, kMd) = ClusterFramesReportWriter.Write(clusterReport, config.ReportDirectory);
        Console.WriteLine();
        Console.WriteLine(ClusterFramesReportWriter.RenderMarkdown(clusterReport));
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Report written to:");
        Console.WriteLine($"  - {kJson}");
        Console.WriteLine($"  - {kMd}");
        if (clusterReport.FixtureError is not null)
        {
            Console.Error.WriteLine($"FIXTURE ERROR: {clusterReport.FixtureError}");
            return 2;
        }

        return clusterReport.Verdict.Passed ? 0 : 1;
    }

    if (!string.Equals(config.Scenario, ExactlyOnceScenario.Name, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Unknown HARNESS_SCENARIO '{config.Scenario}' (exactly-once | conversation-frames | conversation-frames-selfcheck | cluster-frames | cluster-frames-selfcheck).");
    }

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var report = await ExactlyOnceScenario.RunAsync(config, cts.Token);

    var (jsonPath, mdPath) = ScenarioReportWriter.Write(report, config.ReportDirectory);
    Console.WriteLine();
    Console.WriteLine(ScenarioReportWriter.RenderMarkdown(report));
    Console.WriteLine();
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Report written to:");
    Console.WriteLine($"  - {jsonPath}");
    Console.WriteLine($"  - {mdPath}");

    return report.Passed ? 0 : 1;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"CONFIGURATION ERROR: {ex.Message}");
    return 2;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"HTTP ERROR: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"UNEXPECTED ERROR: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 2;
}
