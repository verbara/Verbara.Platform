using System.Net;

namespace Asterisk.Platform.Api.Tests;

public sealed class ErrorHandlingTests : IClassFixture<PlatformApiFactory>
{
    private readonly HttpClient _client;

    public ErrorHandlingTests(PlatformApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task UnknownRoute_ShouldReturn404()
    {
        var response = await _client.GetAsync("/api/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WebhookWithUnknownChannel_ShouldReturnProblemDetails()
    {
        var response = await _client.PostAsync("/api/webhooks/t1/xyz", new ByteArrayContent([]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnprotectedWebhookEndpoint_ShouldNotReturn500_WhenBodyIsEmpty()
    {
        // Empty body to unknown channel gives 400, not 500
        var response = await _client.PostAsync("/api/webhooks/t1/invalidchannel", new ByteArrayContent([]));

        ((int)response.StatusCode).Should().BeLessThan(500);
    }
}
