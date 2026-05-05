using System.Net;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Analytics.Live;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Tests for <see cref="QueueMetricsEndpoints"/> covering R5.1 Task H —
/// wiring of <see cref="ILiveQueueMetricsProvider"/> into
/// <c>GET /operations/queue-metrics</c> with a null/unavailable fallback.
/// </summary>
public sealed class QueueMetricsEndpointTests : IClassFixture<UnifiedPlatformApiFactory>
{
    public QueueMetricsEndpointTests(UnifiedPlatformApiFactory factory) { _ = factory; }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static Queue MakeQueue(string tenantId, string queueId, string name)
        => new()
        {
            QueueId = EntityId.From(queueId),
            TenantId = new TenantId(tenantId),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static async Task SeedQueueAsync(UnifiedPlatformApiFactory factory, string queueId, string queueName)
    {
        var queueStore = factory.Services.GetRequiredService<IQueueStore>();
        await queueStore.SaveAsync(MakeQueue(UnifiedPlatformApiFactory.TestTenantId, queueId, queueName), CancellationToken.None);
    }

    // ─── RBAC ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetQueueMetrics_ShouldReturn401Or403_WhenNotAuthenticated()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateClient(); // no Authorization header

        var response = await client.GetAsync("/api/v1/operations/queue-metrics");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // ─── Null-provider fallback (no Pro.Analytics.Live registered) ───────────

    [Fact]
    public async Task GetQueueMetrics_ShouldReturnNullWaitingAndUnavailableHeader_WhenProviderNotRegistered()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await SeedQueueAsync(factory, "queue-001", "support");

        var response = await client.GetAsync("/api/v1/operations/queue-metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(QueueMetricsEndpoints.MetricsAvailableHeader, out var values)
            .Should().BeTrue();
        values!.Should().Contain("false");

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var arr = json!.AsArray();
        arr.Count.Should().Be(1);
        var first = arr[0]!;
        first["queueName"]!.GetValue<string>().Should().Be("support");
        // With DefaultIgnoreCondition = WhenWritingNull, null fields are omitted
        // from the JSON payload entirely. Both are accepted as "unavailable".
        (first["waiting"] is null || first["waiting"]!.GetValue<int?>() is null).Should().BeTrue();
        (first["avgWaitSeconds"] is null || first["avgWaitSeconds"]!.GetValue<double?>() is null).Should().BeTrue();
    }

    // ─── Provider available + returns metrics ────────────────────────────────

    [Fact]
    public async Task GetQueueMetrics_ShouldReturnRealWaitingAndNoUnavailableHeader_WhenProviderReturnsMetrics()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var stub = Substitute.For<ILiveQueueMetricsProvider>();
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        stub.GetLiveMetricsAsync(Arg.Any<string>(), "support", Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<LiveQueueMetrics?>(new LiveQueueMetrics(
                TenantId: "",
                QueueName: "support",
                CallsWaiting: 7,
                AvgWaitSeconds: 42.5,
                AgentsAvailable: 3,
                CapturedAt: capturedAt)));
#pragma warning restore CA2012

        await using var factory = new UnifiedPlatformApiFactory();
        using var scopedFactory = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<ILiveQueueMetricsProvider>();
            services.AddSingleton(stub);
        }));
        using var client = CreateAuthenticatedClient(scopedFactory);

        var queueStore = scopedFactory.Services.GetRequiredService<IQueueStore>();
        await queueStore.SaveAsync(MakeQueue(UnifiedPlatformApiFactory.TestTenantId, "queue-001", "support"), CancellationToken.None);

        var response = await client.GetAsync("/api/v1/operations/queue-metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(QueueMetricsEndpoints.MetricsAvailableHeader).Should().BeFalse();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var arr = json!.AsArray();
        arr.Count.Should().Be(1);
        var first = arr[0]!;
        first["queueName"]!.GetValue<string>().Should().Be("support");
        first["waiting"]!.GetValue<int>().Should().Be(7);
        first["avgWaitSeconds"]!.GetValue<double>().Should().BeApproximately(42.5, 0.001);
    }

    [Fact]
    public async Task GetQueueMetrics_ShouldSetUnavailableHeader_WhenProviderReturnsNullForAllQueues()
    {
        var stub = Substitute.For<ILiveQueueMetricsProvider>();
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        stub.GetLiveMetricsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<LiveQueueMetrics?>((LiveQueueMetrics?)null));
#pragma warning restore CA2012

        await using var factory = new UnifiedPlatformApiFactory();
        using var scopedFactory = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<ILiveQueueMetricsProvider>();
            services.AddSingleton(stub);
        }));
        using var client = CreateAuthenticatedClient(scopedFactory);

        var queueStore = scopedFactory.Services.GetRequiredService<IQueueStore>();
        await queueStore.SaveAsync(MakeQueue(UnifiedPlatformApiFactory.TestTenantId, "queue-001", "support"), CancellationToken.None);
        await queueStore.SaveAsync(MakeQueue(UnifiedPlatformApiFactory.TestTenantId, "queue-002", "sales"), CancellationToken.None);

        var response = await client.GetAsync("/api/v1/operations/queue-metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(QueueMetricsEndpoints.MetricsAvailableHeader, out var values)
            .Should().BeTrue();
        values!.Should().Contain("false");

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var arr = json!.AsArray();
        arr.Count.Should().Be(2);
        foreach (var item in arr)
        {
            (item!["waiting"] is null || item["waiting"]!.GetValue<int?>() is null).Should().BeTrue();
            (item["avgWaitSeconds"] is null || item["avgWaitSeconds"]!.GetValue<double?>() is null).Should().BeTrue();
        }
    }

    private static HttpClient CreateAuthenticatedClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {UnifiedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", UnifiedPlatformApiFactory.TestTenantId);
        return client;
    }
}
