// PresenceScenario — R5.4 S5.1.
// Target: 1,500 concurrent virtual users (3-node cluster × 500 agents) for
// 3 minutes. Each iteration emits one agent presence heartbeat to validate
// SignalR fan-out + Pro.Push bus + IAgentTenantResolver lateral cache
// invalidation under sustained simultaneous-agent pressure.

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

        return Scenario.Create("presence_broadcast", async ctx =>
            {
                var agentId = $"agent-{ctx.ScenarioInfo.InstanceId}";
                var req = Http.CreateRequest("POST", $"/api/v1/agents/{agentId}/presence")
                    .WithHeader("Authorization", $"Bearer {token}")
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(new StringContent(
                        $$"""{"status":"available","timestamp":"{{DateTimeOffset.UtcNow:O}}"}"""));
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.KeepConstant(
                    copies: 1_500,
                    during: TimeSpan.FromMinutes(3)));
    }
}
