using System.Net;

namespace Asterisk.Platform.Api.Tests;

public sealed class SystemInfoFeatureTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public SystemInfoFeatureTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task SystemInfo_ShouldReturnFeatures_WithKnownKeys()
    {
        var response = await _client.GetAsync("/api/admin/system/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"conversations\":true");
        json.Should().Contain("\"dialer\":false");
        json.Should().Contain("\"queues\":true");
    }
}
