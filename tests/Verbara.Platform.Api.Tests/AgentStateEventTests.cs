using System.Net;
using System.Net.Http.Json;

namespace Verbara.Platform.Api.Tests;

public sealed class AgentStateEventTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public AgentStateEventTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task UpdateAgentState_ShouldReturn200OrNotFound()
    {
        var stateBody = JsonContent.Create(new { state = "Available" });
        var response = await _client.PutAsync("/api/agents/me/state", stateBody);
        // 200 if agent exists, 404 if not — endpoint exists either way
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }
}
