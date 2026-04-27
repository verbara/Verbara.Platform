// LiveQueueWriterScenario — R5.4 S5.1 (R5.5 A.2 amendment — staging tenant + seeded queue range).
// Target: 500 reads/s for 2 minutes against the live-metrics read endpoint
// (5 Hz × 100 queues = 500 queries/s). Validates the Pro.Analytics.Live
// snapshot pipeline end-to-end: PostgresLiveQueueSnapshotStore writes are
// driven by the running platform, this scenario stresses the read surface
// + ILiveQueueMetricsProvider hot-path.

using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Asterisk.Platform.LoadTests.Scenarios;

internal static class LiveQueueWriterScenario
{
    public static ScenarioProps Build(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var token = Environment.GetEnvironmentVariable("LOADTEST_TOKEN") ?? "";
        var tenant = Environment.GetEnvironmentVariable("LOADTEST_TENANT") ?? "loadtest";
        // Fixture tenant has 1 queue called loadtest-{0..N-1}. Seeded staging
        // tenants have queue-1..queue-N (1-indexed). Use the right format.
        var queuePrefix = Environment.GetEnvironmentVariable("LOADTEST_QUEUE_PREFIX")
                          ?? (tenant == "loadtest" ? "loadtest-" : "queue-");
        var queueIndexBase = tenant == "loadtest" ? 0 : 1;
        var queueRange = tenant == "loadtest" ? 100 : 50; // medium-loadtest seeds 50 queues

        return Scenario.Create("live_queue_snapshot_write", async ctx =>
            {
                var queueIdx = queueIndexBase + (ctx.ScenarioInfo.InstanceNumber % queueRange);
                var req = Http.CreateRequest(
                        "GET",
                        $"/api/v1/operations/queues/{queuePrefix}{queueIdx}/live-metrics")
                    .WithHeader("Authorization", $"Bearer {token}")
                    .WithHeader("X-Tenant-Id", tenant);
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: 500,
                    interval: TimeSpan.FromSeconds(1),
                    during: TimeSpan.FromMinutes(2)));
    }
}
