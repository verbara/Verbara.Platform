// LiveQueueWriterScenario — R5.4 S5.1.
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

        return Scenario.Create("live_queue_snapshot_write", async ctx =>
            {
                var queueIdx = ctx.ScenarioInfo.InstanceNumber % 100;
                var req = Http.CreateRequest(
                        "GET",
                        $"/api/v1/operations/queues/loadtest-{queueIdx}/live-metrics")
                    .WithHeader("Authorization", $"Bearer {token}");
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: 500,
                    interval: TimeSpan.FromSeconds(1),
                    during: TimeSpan.FromMinutes(2)));
    }
}
