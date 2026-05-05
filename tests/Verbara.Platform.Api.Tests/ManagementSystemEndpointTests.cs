using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

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
        body.Should().Contain("1.7.0");
    }

    [Fact]
    public async Task License_ShouldReturnLicenseStatus()
    {
        var response = await _client.GetAsync("/api/management/system/license");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("isValid");
        body.Should().Contain("status");
        body.Should().Contain("maxNodes");
    }

    // R5.2 PC.4 / triage limitation #11 — the GET /management/system/license
    // payload must surface the grace-period state so the Web admin StatCard can
    // render the real grace window. The PlatformAdminApiFactory removes the
    // LicenseValidationHostedService (its FullName contains "Asterisk"), so the
    // tracker keeps its initial Invalid state → blocked=true here. The full
    // mapping for GracePeriod / Valid / Expired branches lives in the pure-
    // function tests at Endpoints/ManagementSystemEndpointsTests.
    [Fact]
    public async Task License_ShouldExposeGracePeriodFields_PC4()
    {
        var response = await _client.GetAsync("/api/management/system/license");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("inGrace");
        body.Should().Contain("blocked");
        body.Should().Contain("\"inGrace\":false");
        body.Should().Contain("\"blocked\":true");
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
