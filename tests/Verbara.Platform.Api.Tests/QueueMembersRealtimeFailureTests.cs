using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// ADR-0012 Ola-3 asymmetry fix (R1) — before this change the three RESTful queue-member routes
/// (POST / DELETE / PATCH) resolved <c>IRealtimeSyncService</c> inline and awaited the sync WITHOUT
/// a try/catch, so a realtime-backend failure 500'd the member write (an asymmetry vs the admin
/// paths, which deferred). The sync now rides <c>IQueueMembershipStore</c> as a best-effort
/// decorator, so a throwing realtime backend can no longer fail the write: the member routes return
/// 201 / 204 / 200 as normal. Runs on the shared throwing-sync factory (every sync op throws).
/// </summary>
public sealed class QueueMembersRealtimeFailureTests
    : IClassFixture<AdminEndpointRealtimeFailureTests.ThrowingSyncApiFactory>
{
    private readonly HttpClient _client;

    public QueueMembersRealtimeFailureTests(AdminEndpointRealtimeFailureTests.ThrowingSyncApiFactory factory)
        => _client = factory.CreateAuthenticatedClient();

    [Fact]
    public async Task AddMember_ShouldReturn201_WhenRealtimeSyncThrows()
    {
        var (queueId, agentId) = await SeedQueueAndAgentAsync("qm-add");

        var response = await _client.PostAsync(
            $"/api/v1/queues/{queueId}/members",
            JsonContent.Create(new { agentId, penalty = 2 }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task RemoveMember_ShouldReturn204_WhenRealtimeSyncThrows()
    {
        var (queueId, agentId) = await SeedQueueAndAgentAsync("qm-remove");
        var add = await _client.PostAsync(
            $"/api/v1/queues/{queueId}/members",
            JsonContent.Create(new { agentId, penalty = 0 }));
        add.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await _client.DeleteAsync($"/api/v1/queues/{queueId}/members/{agentId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task UpdateMember_ShouldReturn200_WhenRealtimeSyncThrows()
    {
        var (queueId, agentId) = await SeedQueueAndAgentAsync("qm-update");
        var add = await _client.PostAsync(
            $"/api/v1/queues/{queueId}/members",
            JsonContent.Create(new { agentId, penalty = 0 }));
        add.StatusCode.Should().Be(HttpStatusCode.Created);

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/queues/{queueId}/members/{agentId}")
        {
            Content = JsonContent.Create(new { penalty = 5 }),
        };
        var response = await _client.SendAsync(patch);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<(string QueueId, string AgentId)> SeedQueueAndAgentAsync(string slug)
    {
        var queue = await _client.PostAsync("/api/v1/admin/queues",
            JsonContent.Create(new { name = $"{slug}-queue" }));
        queue.StatusCode.Should().Be(HttpStatusCode.Created);
        var queueId = JsonNode.Parse(await queue.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var agent = await _client.PostAsync("/api/v1/admin/agents",
            JsonContent.Create(new { userId = $"user-{slug}", displayName = $"{slug} Agent" }));
        agent.StatusCode.Should().Be(HttpStatusCode.Created);
        var agentId = JsonNode.Parse(await agent.Content.ReadAsStringAsync())!["agentId"]!.GetValue<string>();

        return (queueId, agentId);
    }
}
