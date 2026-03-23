using System.Net;

namespace Asterisk.Platform.Api.Tests;

public sealed class WrapUpEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public WrapUpEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task WrapUpConversation_ShouldReturn404_WhenConversationDoesNotExist()
    {
        var response = await _client.PostAsync("/api/conversations/nonexistent-id/wrapup", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
