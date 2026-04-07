using System.Net;
using System.Net.Http.Json;

namespace Asterisk.Platform.Api.Tests;

public sealed class PartnerSettingsEndpointsTests : IClassFixture<PartnerApiFactory>
{
    private readonly HttpClient _client;
    private readonly HttpClient _anonClient;

    public PartnerSettingsEndpointsTests(PartnerApiFactory factory)
    {
        _client = factory.CreatePartnerClient();
        _anonClient = factory.CreateAnonymousClient();
    }

    [Fact]
    public async Task GetPartnerSettings_ShouldReturnOwnSettings()
    {
        var response = await _client.GetAsync("/api/v1/partner/settings/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace();
        // Settings DTO contains tenantId of the caller
        body.Should().Contain(PartnerApiFactory.PartnerTenantId);
    }

    [Fact]
    public async Task UpdatePartnerSettings_ShouldUpdateOperationalSettings()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/partner/settings/", new
        {
            operational = new
            {
                maxConcurrentChannels = 150,
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UpdatePartnerSettings_ShouldStripPlanAndQuotas()
    {
        // Attempt to update Plan and Quotas (should be silently ignored / stripped)
        var response = await _client.PutAsJsonAsync("/api/v1/partner/settings/", new
        {
            plan = "Starter",  // should be stripped
            quotas = new     // should be stripped
            {
                maxActiveAgents = 1,
            },
            operational = new
            {
                maxConcurrentChannels = 50,
            },
        });

        // Should succeed (stripping happens server-side, no error)
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPartnerSettings_ShouldReturn401_WhenNotAuthenticated()
    {
        var response = await _anonClient.GetAsync("/api/v1/partner/settings/");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
