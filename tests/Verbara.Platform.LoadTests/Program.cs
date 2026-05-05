// Verbara.Platform.LoadTests — R5.4 S5.1 baseline load test entry point.
//
// Reads PLATFORM_API_URL (default http://localhost:8080) + LOADTEST_TOKEN
// (issued by scripts/load-test.sh after seeding the loadtest tenant), then
// runs scenarios and writes Markdown + CSV + HTML reports under
// ./load-test-reports/<timestamp>/.
//
// Modes (LOADTEST_MODE env var):
//   full              — all 5 scenarios in parallel (default; legacy R5.4 path)
//   jwt-only          — only JwtScenario
//   queues-only       — only QueueIngestionScenario (GET /admin/queues)
//   presence-only     — only PresenceScenario (GET /admin/agents, VU shape)
//   livequeue-only    — only LiveQueueWriterScenario (GET /analytics/live/{q})
//   agentassist-only  — only AgentAssistScenario (GET /admin/teams)
//
// Per-scenario rate / VU + duration overrides (R5.5 C-L sweep harness):
//   LOADTEST_RATE          — overrides Inject rate (req/s) for rate-based scenarios
//                            (jwt, queues, livequeue, agentassist)
//   LOADTEST_VU            — overrides KeepConstant copies for VU-based scenarios
//                            (presence)
//   LOADTEST_DURATION_SEC  — overrides duration for any scenario
//
// To run manually:
//   PLATFORM_API_URL=http://localhost:8080 \
//   LOADTEST_TOKEN="<bearer>" \
//     dotnet run -c Release
//
// Or use the wrapper: ./scripts/load-test.sh
// JWT sweep:           ./scripts/jwt-sweep.sh
// Per-scenario sweep:  ./scripts/scenario-sweep.sh <scenario>

using Verbara.Platform.LoadTests.Scenarios;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

var baseUrl = Environment.GetEnvironmentVariable("PLATFORM_API_URL")
              ?? "http://localhost:8080";
var mode = Environment.GetEnvironmentVariable("LOADTEST_MODE") ?? "full";

var scenarios = mode switch
{
    "jwt-only" => [JwtScenario.Build(baseUrl)],
    "queues-only" => [QueueIngestionScenario.Build(baseUrl)],
    "presence-only" => [PresenceScenario.Build(baseUrl)],
    "livequeue-only" => [LiveQueueWriterScenario.Build(baseUrl)],
    "agentassist-only" => [AgentAssistScenario.Build(baseUrl)],
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
