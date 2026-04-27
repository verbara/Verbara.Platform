// Asterisk.Platform.LoadTests — R5.4 S5.1 baseline load test entry point.
//
// Reads PLATFORM_API_URL (default http://localhost:8080) + LOADTEST_TOKEN
// (issued by scripts/load-test.sh after seeding the loadtest tenant), then
// runs scenarios and writes Markdown + CSV + HTML reports under
// ./load-test-reports/<timestamp>/.
//
// Modes (LOADTEST_MODE env var):
//   full      — all 5 scenarios in parallel (default; legacy R5.4 path)
//   jwt-only  — only JwtScenario; combined with LOADTEST_RATE +
//               LOADTEST_DURATION_SEC env vars this is the Phase B-L
//               JWT rate-sweep mode (R5.5 step 4 in load-test-baseline.md
//               next-steps).
//
// To run manually:
//   PLATFORM_API_URL=http://localhost:8080 \
//   LOADTEST_TOKEN="<bearer>" \
//     dotnet run -c Release
//
// Or use the wrapper: ./scripts/load-test.sh
// JWT sweep: ./scripts/jwt-sweep.sh

using Asterisk.Platform.LoadTests.Scenarios;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

var baseUrl = Environment.GetEnvironmentVariable("PLATFORM_API_URL")
              ?? "http://localhost:8080";
var mode = Environment.GetEnvironmentVariable("LOADTEST_MODE") ?? "full";

var scenarios = mode switch
{
    "jwt-only" => [JwtScenario.Build(baseUrl)],
    _ => new[]
    {
        JwtScenario.Build(baseUrl),
        QueueIngestionScenario.Build(baseUrl),
        PresenceScenario.Build(baseUrl),
        LiveQueueWriterScenario.Build(baseUrl),
        AgentAssistScenario.Build(baseUrl),
    },
};

NBomberRunner
    .RegisterScenarios(scenarios)
    .WithReportFolder("load-test-reports")
    .WithReportFormats(ReportFormat.Md, ReportFormat.Csv, ReportFormat.Html)
    .Run();
