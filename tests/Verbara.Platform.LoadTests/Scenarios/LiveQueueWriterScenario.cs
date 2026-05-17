// LiveQueueWriterScenario — R5.4 S5.1 (R5.5 A.2 + B-L #2 amendment — real
// endpoint reconciled, staging tenant header, SupervisorPlus token via
// LOADTEST_TOKEN).
//
// Target: 500 reads/s for 2 minutes against the live-metrics read endpoint
// (5 Hz × 100 queues = 500 queries/s). Validates the Pro.Analytics.Live
// snapshot pipeline end-to-end: PostgresLiveQueueSnapshotStore writes are
// driven by the running platform, this scenario stresses the read surface
// + ILiveQueueMetricsProvider hot-path.
//
// Real endpoint: GET /api/v1/analytics/live/{queueName} — gated by
// `SupervisorPlus` policy (RequireRole("Admin", "Supervisor")). The
// staging-profile load-test wrapper uses the platform-admin token from
// docker/.staging-admin-token so the policy passes.
//
// R5.5 C-L stress sweep amendment: rate + duration honor LOADTEST_RATE +
// LOADTEST_DURATION_SEC env vars so scripts/scenario-sweep.sh can drive
// per-step isolated runs to find the docker-compose knee on this hardware.

using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Verbara.Platform.LoadTests.Scenarios;

internal static class LiveQueueWriterScenario
{
    public static ScenarioProps Build(string baseUrl)
    {
        var http = Verbara.Platform.LoadTests.Infrastructure.LoadTestHttpClient.Create(baseUrl);
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
                        $"/api/v1/analytics/live/{queuePrefix}{queueIdx}")
                    .WithHeader("Authorization", $"Bearer {token}")
                    .WithHeader("X-Tenant-Id", tenant);
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: int.TryParse(Environment.GetEnvironmentVariable("LOADTEST_RATE"), out var r) ? r : 500,
                    interval: TimeSpan.FromSeconds(1),
                    during: TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("LOADTEST_DURATION_SEC"), out var d) ? d : 120)));
    }
}
