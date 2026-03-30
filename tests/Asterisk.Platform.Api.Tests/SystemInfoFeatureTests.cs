using System.Net;

namespace Asterisk.Platform.Api.Tests;

public sealed class SystemInfoFeatureTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public SystemInfoFeatureTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task SystemInfo_ShouldReturnFeatures_WithKnownKeys()
    {
        var response = await _client.GetAsync("/api/management/system/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"conversations\":true");
        json.Should().Contain("\"dialer\":false");
        json.Should().Contain("\"queues\":true");
    }
}
