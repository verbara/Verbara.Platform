using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asterisk.Platform.Api.Tests;

public sealed class AdminEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public AdminEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── Queues ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListQueues_ShouldReturn200_WithEmptyResult()
    {
        var response = await _client.GetAsync("/api/admin/queues");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateQueue_ShouldReturn201_WithCreatedQueue()
    {
        var body = JsonContent.Create(new { name = "Support Queue" });
        var response = await _client.PostAsync("/api/admin/queues", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Support Queue");
    }

    [Fact]
    public async Task GetQueue_ShouldReturn404_WhenQueueDoesNotExist()
    {
        var response = await _client.GetAsync("/api/admin/queues/nonexistent-queue-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateAndGetQueue_ShouldReturn200_WhenQueueExists()
    {
        var body = JsonContent.Create(new { name = "Sales Queue" });
        var createResponse = await _client.PostAsync("/api/admin/queues", body);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync());
        var queueId = created!["queueId"]!["value"]!.GetValue<string>();

        var getResponse = await _client.GetAsync($"/api/admin/queues/{queueId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await getResponse.Content.ReadAsStringAsync();
        content.Should().Contain("Sales Queue");
    }

    [Fact]
    public async Task UpdateQueue_ShouldReturn404_WhenQueueDoesNotExist()
    {
        var body = JsonContent.Create(new { name = "Updated Name" });
        var response = await _client.PutAsync("/api/admin/queues/no-such-queue", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteQueue_ShouldReturn204()
    {
        // Create first
        var createBody = JsonContent.Create(new { name = "Queue To Delete" });
        var createResp = await _client.PostAsync("/api/admin/queues", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var queueId = created!["queueId"]!["value"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/queues/{queueId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── Users ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListUsers_ShouldReturn200_WithEmptyResult()
    {
        var response = await _client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateUser_ShouldReturn201_WithCreatedUser()
    {
        var body = JsonContent.Create(new { email = "agent@example.com", displayName = "Test Agent", role = 0 });
        var response = await _client.PostAsync("/api/admin/users", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("agent@example.com");
    }

    [Fact]
    public async Task GetUser_ShouldReturn404_WhenUserDoesNotExist()
    {
        var response = await _client.GetAsync("/api/admin/users/no-such-user");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteUser_ShouldReturn204()
    {
        var createBody = JsonContent.Create(new { email = "delete-me@example.com", displayName = "To Delete", role = 0 });
        var createResp = await _client.PostAsync("/api/admin/users", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var userId = created!["userId"]!["value"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/users/{userId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── Agents ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAgents_ShouldReturn200_WithEmptyResult()
    {
        var response = await _client.GetAsync("/api/admin/agents");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateAgent_ShouldReturn201_WithCreatedAgent()
    {
        var body = JsonContent.Create(new { userId = "user-xyz-001", displayName = "Alice Agent" });
        var response = await _client.PostAsync("/api/admin/agents", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Alice Agent");
    }

    [Fact]
    public async Task GetAgent_ShouldReturn404_WhenAgentDoesNotExist()
    {
        var response = await _client.GetAsync("/api/admin/agents/no-such-agent");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Teams ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTeams_ShouldReturn200_WithEmptyResult()
    {
        var response = await _client.GetAsync("/api/admin/teams");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateTeam_ShouldReturn201_WithCreatedTeam()
    {
        var body = JsonContent.Create(new { name = "Alpha Team" });
        var response = await _client.PostAsync("/api/admin/teams", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Alpha Team");
    }

    [Fact]
    public async Task DeleteTeam_ShouldReturn204()
    {
        var createBody = JsonContent.Create(new { name = "Temp Team" });
        var createResp = await _client.PostAsync("/api/admin/teams", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var teamId = created!["teamId"]!["value"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/teams/{teamId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
