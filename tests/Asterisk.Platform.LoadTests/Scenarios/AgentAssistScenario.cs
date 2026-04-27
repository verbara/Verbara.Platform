// AgentAssistScenario — R5.4 S5.1.
// Target: 50 session starts/s for 2 minutes against the AgentAssist
// session-start endpoint. Validates the Pro.AgentAssist runtime feature
// toggle path + per-provider ResiliencePolicy + session/queue gauges
// under sustained spin-up rate.

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

        return Scenario.Create("agent_assist_session_start", async ctx =>
            {
                var sessionId = Guid.NewGuid();
                var req = Http.CreateRequest(
                        "POST",
                        $"/api/v1/agent-assist/sessions/{sessionId}/start")
                    .WithHeader("Authorization", $"Bearer {token}")
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(new StringContent(
                        $$"""{"agentId":"agent-{{ctx.ScenarioInfo.InstanceId}}","queueId":"loadtest-queue"}"""));
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: 50,
                    interval: TimeSpan.FromSeconds(1),
                    during: TimeSpan.FromMinutes(2)));
    }
}
