using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Verifies API versioning behavior:
/// - Unversioned /api/* requests are rewritten to /api/v1/* with a Warning header
/// - Versioned /api/v1/* requests pass through unchanged
/// - Asp.Versioning reports api-supported-versions on all versioned responses
/// </summary>
public sealed class VersioningTests : IClassFixture<PlatformApiFactory>
{
    private readonly HttpClient _client;

    public VersioningTests(PlatformApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task UnversionedRequest_ShouldAddWarningHeader_WhenRewrittenToV1()
    {
        var response = await _client.PostAsJsonAsync("/api/setup", new
        {
            email = "versioning-test@example.com",
            password = "VersionTest2026!",
            displayName = "Versioning Test Admin",
            platformName = "Versioning Test Platform",
        });

        // The middleware rewrites the path; the endpoint will respond 201 (first call) or 409 (tenant exists)
        // A 404 here would mean the path rewrite failed to reach the endpoint
        ((int)response.StatusCode).Should().BeOneOf(201, 409);

        response.Headers.Contains("Warning").Should().BeTrue(
            "the middleware must attach a Warning header to all unversioned requests");

        var warning = response.Headers.GetValues("Warning").First();
        warning.Should().Contain("Unversioned URL deprecated");
    }

    [Fact]
    public async Task UnversionedRequest_ShouldReachSameEndpoint_AsVersionedRequest()
    {
        var unversioned = await _client.PostAsJsonAsync("/api/setup", new
        {
            email = "rewrite-check@example.com",
            password = "RewriteCheck2026!",
        });

        var versioned = await _client.PostAsJsonAsync("/api/v1/setup", new
        {
            email = "rewrite-check@example.com",
            password = "RewriteCheck2026!",
        });

        // Both should hit the same handler; status codes must be identical
        unversioned.StatusCode.Should().Be(versioned.StatusCode,
            "unversioned URL rewrite must route to the same endpoint as the versioned URL");
    }

    [Fact]
    public async Task VersionedRequest_ShouldNotHaveWarningHeader()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup", new
        {
            email = "nowarning@example.com",
            password = "NoWarning2026!",
        });

        ((int)response.StatusCode).Should().BeOneOf(201, 409);

        response.Headers.Contains("Warning").Should().BeFalse(
            "already-versioned requests must not have the deprecation Warning header");
    }

    [Fact]
    public async Task VersionedEndpoint_ShouldReportSupportedVersions()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup", new
        {
            email = "apiversions@example.com",
            password = "ApiVersions2026!",
        });

        ((int)response.StatusCode).Should().BeOneOf(201, 409);

        response.Headers.Contains("api-supported-versions").Should().BeTrue(
            "Asp.Versioning must advertise supported versions on every versioned response");

        var versions = response.Headers.GetValues("api-supported-versions").First();
        versions.Should().Contain("1.0",
            "the API currently exposes only v1.0");
    }

    [Fact]
    public async Task HealthEndpoint_ShouldNotBeAffected_ByVersionRedirect()
    {
        // /health is not under /api/ — the middleware must leave it untouched
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Warning").Should().BeFalse(
            "non-API paths must pass through without the deprecation Warning header");
    }
}
