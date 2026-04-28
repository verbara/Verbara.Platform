// AgentAssistScenario — R5.4 S5.1.
// R5.5 P1 follow-up rewrite (2026-04-27): the original
// POST /api/v1/agent-assist/sessions/{sessionId}/start endpoint never
// existed on the current Platform.Api surface (AgentAssist sessions are
// system-initiated when calls reach AgentAssist-enabled queues, not by
// HTTP). Additionally `/admin/agent-assist/*` endpoints are gated by
// `LicenseFeature.AgentAssist` which is not provisioned on the staging
// stack — direct probing returns 403.
//
// New target: GET /api/v1/admin/teams (paginated list) at 50 req/s for 2
// minutes. What it measures: auth + tenant resolution + Postgres read of
// the teams table at the same medium-rate steady-state the original
// scenario targeted. Scenario name preserved for report continuity.
//
// R5.5 C-L stress sweep amendment: rate + duration honor LOADTEST_RATE +
// LOADTEST_DURATION_SEC env vars so scripts/scenario-sweep.sh can drive
// per-step isolated runs to find the docker-compose knee on this hardware.
//
// True AgentAssist-session measurement requires an Asterisk call drive
// (SIPp 03-queue-join.xml against a provisioned AgentAssist-enabled
// queue with a real LLM provider behind it) — Phase B-L SIP-side prep
// + license provisioning, not Phase B-L HTTP.

using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Asterisk.Platform.LoadTests.Scenarios;

internal static class AgentAssistScenario
{
    public static ScenarioProps Build(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var token = Environment.GetEnvironmentVariable("LOADTEST_TOKEN") ?? "";
        var tenant = Environment.GetEnvironmentVariable("LOADTEST_TENANT") ?? "loadtest";

        return Scenario.Create("agent_assist_session_start", async ctx =>
            {
                var req = Http.CreateRequest("GET", "/api/v1/admin/teams?pageSize=20")
                    .WithHeader("Authorization", $"Bearer {token}")
                    .WithHeader("X-Tenant-Id", tenant);
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: int.TryParse(Environment.GetEnvironmentVariable("LOADTEST_RATE"), out var r) ? r : 50,
                    interval: TimeSpan.FromSeconds(1),
                    during: TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("LOADTEST_DURATION_SEC"), out var d) ? d : 120)));
    }
}
