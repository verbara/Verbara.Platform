using System.Net;

namespace Verbara.Platform.Api.Tests.Observability;

/// <summary>
/// Guards the observable behaviour of <c>app.MapPrometheusScrapingEndpoint()</c>: a scrape of
/// <c>/metrics</c> must carry metric samples, not just a 200. Verbara.Sdk.OpenTelemetry 2.7.0 raised
/// <c>OpenTelemetry</c> to 1.19.1 while still pulling <c>OpenTelemetry.Exporter.Prometheus.AspNetCore</c>
/// 1.15.2-beta.1, whose serializer calls <c>OpenTelemetry.Internal.MathHelper</c> (gone in 1.19.1):
/// the exporter swallowed the failure and every scrape answered 200 with an empty body.
/// </summary>
public sealed class PrometheusScrapingEndpointTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private readonly UnifiedPlatformApiFactory _factory;

    public PrometheusScrapingEndpointTests(UnifiedPlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetMetrics_ShouldReturnMetricSamples_WhenRequestsWereServed()
    {
        var client = _factory.CreateAuthenticatedClient();

        // Serve a few requests so the ASP.NET Core hosting meter records
        // http.server.request.duration samples for the scrape to export.
        for (var i = 0; i < 3; i++)
            (await client.GetAsync("/health")).Dispose();

        using var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeEmpty("a Prometheus scrape after served requests must carry samples");
        var sampleLines = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToList();
        sampleLines.Should().NotBeEmpty("the body must hold at least one metric sample line");
        body.Should().Contain("http_server_request_duration_seconds");
    }
}
