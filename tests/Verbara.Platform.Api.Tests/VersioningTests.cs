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
            customerTenantId = "versioning-co",
            customerName = "Versioning Co",
            customerAdminEmail = "ops@versioning.example.com",
            customerAdminPassword = "VersionCustomer2026!",
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
        // Both calls use the same email; the SECOND must see the tenant the first created → 409.
        // This proves both routes hit the same handler (same InMemory store).
        var versioned = await _client.PostAsJsonAsync("/api/v1/setup", new
        {
            email = "rewrite-check@example.com",
            password = "RewriteCheck2026!",
            customerTenantId = "rewrite-co",
            customerName = "Rewrite Co",
            customerAdminEmail = "ops@rewrite.example.com",
            customerAdminPassword = "RewriteCustomer2026!",
        });

        var unversioned = await _client.PostAsJsonAsync("/api/setup", new
        {
            email = "rewrite-check@example.com",
            password = "RewriteCheck2026!",
            customerTenantId = "rewrite-co",
            customerName = "Rewrite Co",
            customerAdminEmail = "ops@rewrite.example.com",
            customerAdminPassword = "RewriteCustomer2026!",
        });

        // First call creates (201 or 409 if prior test ran); second must match or be 409.
        ((int)versioned.StatusCode).Should().BeOneOf(201, 409);
        unversioned.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "unversioned URL rewrite must route to the same endpoint as the versioned URL");
    }

    [Fact]
    public async Task VersionedRequest_ShouldNotHaveWarningHeader()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup", new
        {
            email = "nowarning@example.com",
            password = "NoWarning2026!",
            customerTenantId = "nowarning-co",
            customerName = "NoWarning Co",
            customerAdminEmail = "ops@nowarning.example.com",
            customerAdminPassword = "NoWarningCustomer2026!",
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
            customerTenantId = "apiversions-co",
            customerName = "ApiVersions Co",
            customerAdminEmail = "ops@apiversions.example.com",
            customerAdminPassword = "ApiVersionsCustomer2026!",
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
