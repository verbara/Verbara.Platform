// PresenceScenario — R5.4 S5.1 (R5.5 A.2 amendment — staging tenant header).
// Target: 1,500 concurrent virtual users (3-node cluster × 500 agents) for
// 3 minutes. Each iteration emits one agent presence heartbeat to validate
// SignalR fan-out + Pro.Push bus + IAgentTenantResolver lateral cache
// invalidation under sustained simultaneous-agent pressure.
//
// Note: agentId is synthesized per-VU and may not match a seeded agent
// (agent IDs are GUIDs); the API surface still receives the request and
// the latency / throughput metrics remain valid. Success rate may be low
// (404 on unknown agentId) — interpret throughput, not success%.

using System.Text;
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
                var agentId = $"agent-{ctx.ScenarioInfo.InstanceId}";
                var req = Http.CreateRequest("POST", $"/api/v1/agents/{agentId}/presence")
                    .WithHeader("Authorization", $"Bearer {token}")
                    .WithHeader("X-Tenant-Id", tenant)
                    .WithBody(new StringContent(
                        $$"""{"status":"available","timestamp":"{{DateTimeOffset.UtcNow:O}}"}""",
                        Encoding.UTF8,
                        "application/json"));
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.KeepConstant(
                    copies: 1_500,
                    during: TimeSpan.FromMinutes(3)));
    }
}
