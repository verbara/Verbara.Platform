using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Asterisk.Platform.Api.Tests;

public sealed class AdminEndpointRealtimeTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public AdminEndpointRealtimeTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── CreateAgent with Extension + SipPassword ─────────────────────────────

    [Fact]
    public async Task CreateAgent_ShouldReturn201_WithExtensionAndSipPassword()
    {
        var body = JsonContent.Create(new
        {
            userId = "user-realtime-001",
            displayName = "Realtime Agent",
            extension = "2001",
            sipPassword = "secret123"
        });

        var response = await _client.PostAsync("/api/admin/agents", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Realtime Agent");
        content.Should().Contain("2001");
    }

    [Fact]
    public async Task CreateAgent_ShouldMapExtension_ToAgentModel()
    {
        var body = JsonContent.Create(new
        {
            userId = "user-realtime-002",
            displayName = "Extension Agent",
            extension = "3001",
            sipPassword = "pass456"
        });

        var response = await _client.PostAsync("/api/admin/agents", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["extension"]!.GetValue<string>().Should().Be("3001");
        json!["sipPassword"]!.GetValue<string>().Should().Be("pass456");
    }

    [Fact]
    public async Task CreateAgent_WithoutExtension_ShouldReturn201_AndSkipSync()
    {
        var body = JsonContent.Create(new
        {
            userId = "user-no-ext-001",
            displayName = "Agent Without Extension"
        });

        var response = await _client.PostAsync("/api/admin/agents", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Agent Without Extension");
    }

    // ─── DeleteAgent ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAgent_ShouldReturn204_WhenAgentExists()
    {
        // Create first
        var createBody = JsonContent.Create(new
        {
            userId = "user-delete-001",
            displayName = "Agent To Delete",
            extension = "4001",
            sipPassword = "del-secret"
        });
        var createResp = await _client.PostAsync("/api/admin/agents", createBody);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var agentId = created!["agentId"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/agents/{agentId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteAgent_ShouldReturn404_WhenAgentDoesNotExist()
    {
        var response = await _client.DeleteAsync("/api/admin/agents/no-such-agent-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteAgent_ShouldRemoveAgent_SoGetReturns404()
    {
        // Create
        var createBody = JsonContent.Create(new
        {
            userId = "user-delete-verify-001",
            displayName = "Agent Verify Delete"
        });
        var createResp = await _client.PostAsync("/api/admin/agents", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var agentId = created!["agentId"]!.GetValue<string>();

        // Delete
        var deleteResp = await _client.DeleteAsync($"/api/admin/agents/{agentId}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify gone
        var getResp = await _client.GetAsync($"/api/admin/agents/{agentId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
