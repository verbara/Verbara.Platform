// PresenceScenario — R5.4 S5.1.
// R5.5 P1 follow-up rewrite (2026-04-27): the original POST /api/v1/agents/
// {agentId}/presence endpoint never existed on the current Platform.Api
// surface (agent presence is SignalR-driven via PlatformHub, not REST).
// The scenario was producing 100 % 404s — useless signal under sustained
// load.
//
// New target: GET /api/v1/admin/agents (paginated list) at 1 500 concurrent
// virtual users for 3 minutes. What it measures: auth + tenant resolution +
// Postgres read of the agents table + JSON serialization at very high
// concurrency. The 1 500 VU rate intentionally exceeds the single-instance
// JWT-throughput knee (50 – 75 req/s sustained per load-test-baseline.md);
// the scenario is meant to show how a Tier-Large stampede-class load shape
// behaves on dev-workstation hardware, not to hit a SLO.
//
// True presence-fanout measurement requires a dedicated SignalR client
// load tool (e.g. NBomber WebSocket plugin or @microsoft/signalr-based
// JS load harness) — Phase C-L follow-up, not Phase B-L HTTP.

using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Asterisk.Platform.LoadTests.Scenarios;

internal static class PresenceScenario
{
    public static ScenarioProps Build(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var token = Environment.GetEnvironmentVariable("LOADTEST_TOKEN") ?? "";
        var tenant = Environment.GetEnvironmentVariable("LOADTEST_TENANT") ?? "loadtest";

        return Scenario.Create("presence_broadcast", async ctx =>
            {
                var req = Http.CreateRequest("GET", "/api/v1/admin/agents?pageSize=20")
                    .WithHeader("Authorization", $"Bearer {token}")
                    .WithHeader("X-Tenant-Id", tenant);
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.KeepConstant(
                    copies: 1_500,
                    during: TimeSpan.FromMinutes(3)));
    }
}
