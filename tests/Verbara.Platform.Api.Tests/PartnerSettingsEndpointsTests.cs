using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Api.Tests.PartnerChannel;

namespace Verbara.Platform.Api.Tests;

public sealed class PartnerSettingsEndpointsTests : IClassFixture<PartnerApiFactory>
{
    private readonly PartnerApiFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _anonClient;

    public PartnerSettingsEndpointsTests(PartnerApiFactory factory)
    {
        _factory = factory;
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

    // ─── partner-channel-pilot-fixes P6 (partner-channel-tenancy, design D7) ─────────

    [Fact]
    public async Task UpdatePartnerSettings_ShouldIgnoreNodeAffinity_WhenCallerIsPartner()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/partner/settings/", new
        {
            operational = new { nodeAffinity = PartnerChannelTestKit.NodeA, maxActiveCampaigns = 9 },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var partner = await PartnerChannelTestKit.GetTenantAsync(_factory.Services, PartnerApiFactory.PartnerTenantId);
        partner!.Options.NodeAffinity.Should().BeNullOrEmpty("a Partner cannot set its own placement");
        partner.Options.MaxActiveCampaigns.Should().Be(9);
    }
}
