// QueueIngestionScenario — R5.4 S5.1.
// R5.5 P1 follow-up rewrite (2026-04-27): the original POST /api/v1/queues/
// {id}/calls endpoint never existed on the current Platform.Api surface
// (queue ingestion is SIP-driven via Asterisk PJSIP, not an HTTP REST path).
// The scenario was producing 100 % 404s under load — wrong signal entirely.
//
// New target: GET /api/v1/admin/queues (paginated list).
// What it measures: auth + tenant resolution + Postgres read of the queues
// table + JSON serialization at the same 17 req/s steady-state rate
// (~1 000 reads/min for 5 min). Scenario name preserved for report
// continuity, even though the behavior is now read-flavored.
//
// True queue-ingestion measurement requires SIPp driving inbound calls
// via tests/sipp-scenarios/03-queue-join.xml against a provisioned
// Asterisk PJSIP endpoint — Phase B-L SIP-side prep, not Phase B-L HTTP.

using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Asterisk.Platform.LoadTests.Scenarios;

internal static class QueueIngestionScenario
{
    public static ScenarioProps Build(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var token = Environment.GetEnvironmentVariable("LOADTEST_TOKEN") ?? "";
        var tenant = Environment.GetEnvironmentVariable("LOADTEST_TENANT") ?? "loadtest";

        return Scenario.Create("queue_ingestion", async ctx =>
            {
                var req = Http.CreateRequest("GET", "/api/v1/admin/queues?pageSize=20")
                    .WithHeader("Authorization", $"Bearer {token}")
                    .WithHeader("X-Tenant-Id", tenant);
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: 17,
                    interval: TimeSpan.FromSeconds(1),
                    during: TimeSpan.FromMinutes(5))); // ~1,000 / min
    }
}
