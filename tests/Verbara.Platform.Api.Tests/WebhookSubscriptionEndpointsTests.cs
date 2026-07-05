using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

public sealed class WebhookSubscriptionEndpointsTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private const string ValidEventType = "conversation.assigned";
    private static readonly string[] s_invalidEventTypes = ["not.a.real.event"];
    private readonly HttpClient _client;

    public WebhookSubscriptionEndpointsTests(UnifiedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── Create ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSubscription_ShouldReturn201_WhenRequestValid()
    {
        var name = $"hook-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new
        {
            name,
            endpointUrl = "https://example.com/webhooks/in",
            eventTypes = new[] { ValidEventType },
        });

        var response = await _client.PostAsync("/api/webhooks/subscriptions", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["subscriptionId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        // Secret is returned in full on creation only (unmasked).
        json["secret"]!.GetValue<string>().Should().NotContain("...");
        json["eventTypes"]!.AsArray().Count.Should().Be(1);
        json["isActive"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CreateSubscription_ShouldReturn400_WhenNameMissing()
    {
        var body = JsonContent.Create(new
        {
            name = "",
            endpointUrl = "https://example.com/in",
            eventTypes = new[] { ValidEventType },
        });

        var response = await _client.PostAsync("/api/webhooks/subscriptions", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSubscription_ShouldReturn400_WhenEndpointUrlMissing()
    {
        var body = JsonContent.Create(new
        {
            name = $"hook-{Guid.NewGuid():N}",
            endpointUrl = "",
            eventTypes = new[] { ValidEventType },
        });

        var response = await _client.PostAsync("/api/webhooks/subscriptions", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSubscription_ShouldReturn400_WhenEndpointUrlNotHttps()
    {
        var body = JsonContent.Create(new
        {
            name = $"hook-{Guid.NewGuid():N}",
            endpointUrl = "http://insecure.example.com/in",
            eventTypes = new[] { ValidEventType },
        });

        var response = await _client.PostAsync("/api/webhooks/subscriptions", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSubscription_ShouldReturn400_WhenEventTypesEmpty()
    {
        var body = JsonContent.Create(new
        {
            name = $"hook-{Guid.NewGuid():N}",
            endpointUrl = "https://example.com/in",
            eventTypes = Array.Empty<string>(),
        });

        var response = await _client.PostAsync("/api/webhooks/subscriptions", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSubscription_ShouldReturn400_WhenEventTypeInvalid()
    {
        var body = JsonContent.Create(new
        {
            name = $"hook-{Guid.NewGuid():N}",
            endpointUrl = "https://example.com/in",
            eventTypes = s_invalidEventTypes,
        });

        var response = await _client.PostAsync("/api/webhooks/subscriptions", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── List / Get ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListSubscriptions_ShouldReturn200_WhenSubscriptionExists()
    {
        var id = await CreateSubscriptionAsync();

        var response = await _client.GetAsync("/api/webhooks/subscriptions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        var created = items.FirstOrDefault(n => n!["subscriptionId"]!.GetValue<string>() == id);
        created.Should().NotBeNull();
        // Secret is masked on list (format: "xxxxxxxx...xxxx").
        created!["secret"]!.GetValue<string>().Should().Contain("...");
    }

    [Fact]
    public async Task GetSubscription_ShouldReturn200_WhenSubscriptionExists()
    {
        var id = await CreateSubscriptionAsync();

        var response = await _client.GetAsync($"/api/webhooks/subscriptions/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["subscriptionId"]!.GetValue<string>().Should().Be(id);
        json["secret"]!.GetValue<string>().Should().Contain("...");
    }

    [Fact]
    public async Task GetSubscription_ShouldReturn404_WhenIdUnknown()
    {
        var response = await _client.GetAsync($"/api/webhooks/subscriptions/{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Update ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateSubscription_ShouldReturn200_WhenChangesValid()
    {
        var id = await CreateSubscriptionAsync();
        var newName = $"renamed-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new { name = newName, isActive = false });

        var response = await _client.PutAsync($"/api/webhooks/subscriptions/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["name"]!.GetValue<string>().Should().Be(newName);
        json["isActive"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task UpdateSubscription_ShouldReturn404_WhenIdUnknown()
    {
        var body = JsonContent.Create(new { name = "whatever" });

        var response = await _client.PutAsync(
            $"/api/webhooks/subscriptions/{Guid.NewGuid():N}", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateSubscription_ShouldReturn400_WhenEndpointUrlNotHttps()
    {
        var id = await CreateSubscriptionAsync();
        var body = JsonContent.Create(new { endpointUrl = "http://insecure.example.com/in" });

        var response = await _client.PutAsync($"/api/webhooks/subscriptions/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateSubscription_ShouldReturn400_WhenEventTypesEmpty()
    {
        var id = await CreateSubscriptionAsync();
        var body = JsonContent.Create(new { eventTypes = Array.Empty<string>() });

        var response = await _client.PutAsync($"/api/webhooks/subscriptions/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateSubscription_ShouldReturn400_WhenEventTypeInvalid()
    {
        var id = await CreateSubscriptionAsync();
        var body = JsonContent.Create(new { eventTypes = s_invalidEventTypes });

        var response = await _client.PutAsync($"/api/webhooks/subscriptions/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Delete ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSubscription_ShouldReturn204_WhenSubscriptionExists()
    {
        var id = await CreateSubscriptionAsync();

        var response = await _client.DeleteAsync($"/api/webhooks/subscriptions/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteSubscription_ShouldReturn404_WhenIdUnknown()
    {
        var response = await _client.DeleteAsync(
            $"/api/webhooks/subscriptions/{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Test / Deliveries ───────────────────────────────────────────────────

    [Fact]
    public async Task TestSubscription_ShouldReturn200_WhenSubscriptionExists()
    {
        var id = await CreateSubscriptionAsync();

        var response = await _client.PostAsync(
            $"/api/webhooks/subscriptions/{id}/test", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["message"]!.GetValue<string>().Should().Contain("delivery");
    }

    [Fact]
    public async Task ListDeliveries_ShouldReturn200_WhenSubscriptionExists()
    {
        var id = await CreateSubscriptionAsync();
        // Queue one delivery so the paged result is populated.
        (await _client.PostAsync($"/api/webhooks/subscriptions/{id}/test", content: null))
            .EnsureSuccessStatusCode();

        var response = await _client.GetAsync($"/api/webhooks/subscriptions/{id}/deliveries?page=1&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["items"]!.AsArray().Count.Should().BeGreaterThanOrEqualTo(1);
        json["totalCount"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(1);
    }

    // ─── Rotate secret / Circuit breaker ─────────────────────────────────────

    [Fact]
    public async Task RotateSecret_ShouldReturn200_WhenSubscriptionExists()
    {
        var id = await CreateSubscriptionAsync();

        var response = await _client.PostAsync(
            $"/api/webhooks/subscriptions/{id}/rotate-secret", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["subscriptionId"]!.GetValue<string>().Should().Be(id);
        // New secret is returned in full after rotation (unmasked).
        json["secret"]!.GetValue<string>().Should().NotContain("...");
    }

    [Fact]
    public async Task ResetCircuit_ShouldReturn200_WhenSubscriptionExists()
    {
        var id = await CreateSubscriptionAsync();

        var response = await _client.PostAsync(
            $"/api/webhooks/subscriptions/{id}/reset-circuit", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["message"]!.GetValue<string>().Should().Contain(id);
    }

    [Fact]
    public async Task GetCircuitStatus_ShouldReturn200_WhenSubscriptionExists()
    {
        var id = await CreateSubscriptionAsync();

        var response = await _client.GetAsync(
            $"/api/webhooks/subscriptions/{id}/circuit-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["subscriptionId"]!.GetValue<string>().Should().Be(id);
        json["status"]!.GetValue<string>().Should().Be("Closed");
        json["failures"]!.GetValue<int>().Should().Be(0);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<string> CreateSubscriptionAsync(
        string? name = null, string endpointUrl = "https://example.com/webhooks/in")
    {
        var body = JsonContent.Create(new
        {
            name = name ?? $"hook-{Guid.NewGuid():N}",
            endpointUrl,
            eventTypes = new[] { ValidEventType },
        });
        var resp = await _client.PostAsync("/api/webhooks/subscriptions", body);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return json["subscriptionId"]!.GetValue<string>();
    }
}
