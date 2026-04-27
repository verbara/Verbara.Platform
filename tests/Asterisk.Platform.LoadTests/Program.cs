// Asterisk.Platform.LoadTests — R5.4 S5.1 baseline load test entry point.
//
// Reads PLATFORM_API_URL (default http://localhost:8080) + LOADTEST_TOKEN
// (issued by scripts/load-test.sh after seeding the loadtest tenant), then
// runs all five baseline scenarios sequentially and writes Markdown + CSV
// + HTML reports under ./load-test-reports/<timestamp>/.
//
// To run manually:
//   PLATFORM_API_URL=http://localhost:8080 \
//   LOADTEST_TOKEN="<bearer>" \
//     dotnet run -c Release
//
// Or use the wrapper: ./scripts/load-test.sh

using Asterisk.Platform.LoadTests.Scenarios;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

var baseUrl = Environment.GetEnvironmentVariable("PLATFORM_API_URL")
              ?? "http://localhost:8080";

var scenarios = new[]
{
    JwtScenario.Build(baseUrl),
    QueueIngestionScenario.Build(baseUrl),
    PresenceScenario.Build(baseUrl),
    LiveQueueWriterScenario.Build(baseUrl),
    AgentAssistScenario.Build(baseUrl),
};

NBomberRunner
    .RegisterScenarios(scenarios)
    .WithReportFolder("load-test-reports")
    .WithReportFormats(ReportFormat.Md, ReportFormat.Csv, ReportFormat.Html)
    .Run();
