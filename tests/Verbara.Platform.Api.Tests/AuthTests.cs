using System.Net;

namespace Verbara.Platform.Api.Tests;

public sealed class AuthTests : IClassFixture<PlatformApiFactory>
{
    private readonly HttpClient _client;

    public AuthTests(PlatformApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetConversations_ShouldReturn401_WhenNoApiKey()
    {
        var response = await _client.GetAsync("/api/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAgentMe_ShouldReturn401_WhenNoApiKey()
    {
        var response = await _client.GetAsync("/api/agents/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAdminUsers_ShouldReturn401_WhenNoApiKey()
    {
        var response = await _client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAdminQueues_ShouldReturn401_WhenNoApiKey()
    {
        var response = await _client.GetAsync("/api/admin/queues");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SseStream_ShouldReturn401_WhenNoApiKey()
    {
        var response = await _client.GetAsync("/api/events/stream");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetConversations_ShouldReturn200_WhenValidApiKey()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAdminUsers_ShouldReturn200_WhenValidApiKey()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetConversations_ShouldReturn401_WhenInvalidApiKey()
    {
        var client = _client;
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer invalid-key-xyz");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-1");

        var response = await client.GetAsync("/api/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
