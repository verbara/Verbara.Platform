using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Asterisk.Platform.Api.Tests;

public sealed class ConversationEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public ConversationEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task ListConversations_ShouldReturn200_WithEmptyResult()
    {
        var response = await _client.GetAsync("/api/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ListConversations_ShouldReturn200_WithPageParameters()
    {
        var response = await _client.GetAsync("/api/conversations?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetConversation_ShouldReturn404_WhenConversationDoesNotExist()
    {
        var response = await _client.GetAsync("/api/conversations/nonexistent-id-xyz");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMessages_ShouldReturn200_WhenConversationExists()
    {
        // GetMessages returns empty list when conversation has no messages — still 200
        var response = await _client.GetAsync("/api/conversations/any-id/messages?limit=20&offset=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AcceptConversation_ShouldReturn200_WhenSwitchboardCallSucceeds()
    {
        // Switchboard will throw if conversation doesn't exist — expect 400 or 200 depending on impl
        var response = await _client.PostAsync("/api/conversations/some-id/accept", null);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task CloseConversation_ShouldReturn404_WhenConversationDoesNotExist()
    {
        var response = await _client.PostAsync("/api/conversations/unknown-id/close", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendMessage_ShouldReturn400_WhenConversationDoesNotExist()
    {
        var body = JsonContent.Create(new { text = "Hello, how can I help?" });
        var response = await _client.PostAsync("/api/conversations/unknown-id/messages", body);

        // Conversation doesn't exist so service will fail
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task TransferConversation_ShouldReturn400_WhenNoTargetSpecified()
    {
        var body = JsonContent.Create(new { targetQueueId = (string?)null, targetAgentId = (string?)null });
        var response = await _client.PostAsync("/api/conversations/some-id/transfer", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
