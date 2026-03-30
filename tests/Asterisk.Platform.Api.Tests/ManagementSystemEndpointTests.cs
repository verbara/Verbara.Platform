using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class ManagementSystemEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementSystemEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task SystemInfo_ShouldReturnHostTenantId()
    {
        var response = await _client.GetAsync("/api/management/system/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("platform");
        body.Should().Contain("1.1.0");
    }

    [Fact]
    public async Task License_ShouldReturnCommunityTier()
    {
        var response = await _client.GetAsync("/api/management/system/license");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("community");
    }

    [Fact]
    public async Task Settings_ShouldPersistRoundTrip()
    {
        await _client.PutAsJsonAsync("/api/management/system/settings", new
        {
            platformName = "Updated Platform",
            defaultTimezone = "America/Bogota",
            defaultLanguage = "es-CO",
        });

        var response = await _client.GetAsync("/api/management/system/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Updated Platform");
        body.Should().Contain("America/Bogota");
    }
}
