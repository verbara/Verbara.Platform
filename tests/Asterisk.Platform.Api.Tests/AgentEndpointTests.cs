using System.Net;
using System.Net.Http.Json;

namespace Asterisk.Platform.Api.Tests;

public sealed class AgentEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;
    private readonly AuthenticatedPlatformApiFactory _factory;

    public AgentEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetCurrentAgent_ShouldReturn404_WhenNoAgentForUser()
    {
        // The test API key maps to key-id, not a user-id with an agent record.
        var response = await _client.GetAsync("/api/agents/me");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateAgentState_ShouldReturn404_WhenNoAgentForUser()
    {
        var body = JsonContent.Create(new { state = 1 }); // Available
        var response = await _client.PutAsync("/api/agents/me/state", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCurrentAgent_ShouldBeUnauthorized_WhenNoApiKey()
    {
        // Use factory's unauthenticated client (no Authorization header)
        var unauthClient = _factory.CreateClient();
        var response = await unauthClient.GetAsync("/api/agents/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
