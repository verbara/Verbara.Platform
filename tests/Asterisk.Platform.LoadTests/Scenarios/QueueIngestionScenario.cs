// QueueIngestionScenario — R5.4 S5.1.
// Target: ~1,000 inbound calls/min (17 req/s) sustained for 5 minutes
// against POST /api/v1/queues/{id}/calls. Validates Queues.Core ingestion
// throughput + IRoutingMiddlewareBase chain + push fan-out.

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

        return Scenario.Create("queue_ingestion", async ctx =>
            {
                var req = Http.CreateRequest("POST", "/api/v1/queues/loadtest-queue/calls")
                    .WithHeader("Authorization", $"Bearer {token}")
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(new StringContent(
                        $$"""{"callerId":"{{Guid.NewGuid()}}","priority":1}"""));
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: 17,
                    interval: TimeSpan.FromSeconds(1),
                    during: TimeSpan.FromMinutes(5))); // ~1,000 / min
    }
}
