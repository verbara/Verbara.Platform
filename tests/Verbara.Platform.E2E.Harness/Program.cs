using Verbara.Platform.E2E.Harness.Config;
using Verbara.Platform.E2E.Harness.Reports;
using Verbara.Platform.E2E.Harness.Scenarios;

// Walking-skeleton entrypoint: single scenario "exactly-once".
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

try
{
    var config = HarnessConfig.FromEnvironment();
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Verbara.Platform.E2E.Harness — scenario 'exactly-once', topology 'talos'.");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Api:      {config.ApiBaseUrl}");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Hub:      {config.RealtimeHubUrl}");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Tenant:   {config.Tenant}");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Pods:     {config.AuditBaseUrls.Count} ({string.Join(", ", config.AuditBaseUrls)})");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Clients:  {config.ClientCount}");
    Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Events:   {config.EventCount}");
    Console.WriteLine();

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
