using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Observability;

/// <summary>
/// Guards the observable behaviour of <c>app.MapPrometheusScrapingEndpoint()</c>: a scrape of
/// <c>/metrics</c> must carry metric samples, not just a 200. Verbara.Sdk.OpenTelemetry 2.7.0 raised
/// <c>OpenTelemetry</c> to 1.19.1 while still pulling <c>OpenTelemetry.Exporter.Prometheus.AspNetCore</c>
/// 1.15.2-beta.1, whose serializer calls <c>OpenTelemetry.Internal.MathHelper</c> (gone in 1.19.1):
/// the exporter swallowed the failure and every scrape answered 200 with an empty body.
/// </summary>
/// <remarks>
/// The sign-in case runs on <see cref="PlatformApiFactory"/>: its <c>IUserStore</c> is the real in-memory
/// store, which finds a user by email, while <see cref="UnifiedPlatformApiFactory"/> stubs the store with
/// a substitute that cannot.
/// </remarks>
public sealed class PrometheusScrapingEndpointTests
    : IClassFixture<UnifiedPlatformApiFactory>, IClassFixture<PlatformApiFactory>
{
    private readonly UnifiedPlatformApiFactory _factory;
    private readonly PlatformApiFactory _inMemoryFactory;

    public PrometheusScrapingEndpointTests(UnifiedPlatformApiFactory factory, PlatformApiFactory inMemoryFactory)
    {
        _factory = factory;
        _inMemoryFactory = inMemoryFactory;
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

    // measure-api-multi-replica-baseline (D1): the auth write queue's meter must be enrolled, so a
    // sign-in's deferred writes show on the scrape.
    [Fact]
    public async Task GetMetrics_ShouldExportAuthWriteEnqueuedCounter_WhenUserSignsIn()
    {
        const string password = "Mrb-Scrape-Passw0rd!";
        var user = new User
        {
            UserId = EntityId.From($"mrb-scrape-{Guid.NewGuid():N}"),
            TenantId = new TenantId($"mrb-{Guid.NewGuid():N}"[..20]),
            Email = $"mrb-scrape-{Guid.NewGuid():N}@metrics.test",
            DisplayName = "Metrics scrape",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            PasswordHash = PasswordService.HashPassword(password),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using (var scope = _inMemoryFactory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUserStore>().CreateAsync(user, CancellationToken.None);

        using var client = _inMemoryFactory.CreateClient();
        using var signIn = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            tenantId = user.TenantId.Value,
            email = user.Email,
            password,
        });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK, "the scrape must follow a successful sign-in");

        using var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(
            "auth_write_enqueued_total",
            "a successful sign-in enqueues its deferred writes, and the write-queue meter is exported");
    }
}
