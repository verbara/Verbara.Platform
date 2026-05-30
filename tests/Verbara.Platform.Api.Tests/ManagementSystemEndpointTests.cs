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
        // Version flows from the central PackageVersion (Directory.Build.props) via
        // <Version>$(PackageVersion)</Version> in the Api csproj — must be a real
        // semver, never the old hardcoded "1.7.0" (the bug fixed pre-v2.6.0) nor the
        // "0.0.0" MSBuild fallback.
        body.Should().MatchRegex("\"version\":\"\\d+\\.\\d+\\.\\d+");
        body.Should().NotContain("1.7.0");
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

    // Pro v2.4.0-pro — `GET /management/system/license/status` returns the raw
    // ILicenseStatusReader.Snapshot() output (no Platform DTO wrapper). Sibling
    // endpoint to `GET /management/system/license` — surfaces the upstream
    // contract directly so admin tooling reads it as the canonical view.

    [Fact]
    public async Task LicenseStatus_ShouldReturnSnapshot_WhenPlatformAdmin()
    {
        var response = await _client.GetAsync("/api/management/system/license/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task LicenseStatus_ShouldExposeSnapshotFields()
    {
        var response = await _client.GetAsync("/api/management/system/license/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        // Required fields from LicenseStatusSnapshot (Pro v2.4.0-pro contract via
        // LicensingJsonContext — serialized via ApiJsonContext registration).
        body.Should().Contain("isLoaded");
        body.Should().Contain("isValid");
        body.Should().Contain("tier");
        body.Should().Contain("lastValidationResult");
        body.Should().Contain("authorizedDigestsCount");
        // The factory removes LicenseValidationHostedService, so the tracker keeps
        // its initial Invalid state — assert the unloaded shape.
        body.Should().Contain("\"isLoaded\":false");
        body.Should().Contain("\"isValid\":false");
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
