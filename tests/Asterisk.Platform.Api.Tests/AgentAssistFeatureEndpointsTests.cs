using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Asterisk.Sdk.Pro.AgentAssist.Features;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// HTTP contract tests for the AgentAssist runtime feature endpoint surface
/// (<c>GET/PUT /api/v1/admin/features/agent-assist</c>) introduced in R5.1 Task J.
/// Covers happy paths, auth gates, and validation rules — credentials must never
/// leak through GET, PUT without credentials should be rejected, etc.
/// </summary>
public sealed class AgentAssistFeatureEndpointsTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private readonly UnifiedPlatformApiFactory _factory;

    public AgentAssistFeatureEndpointsTests(UnifiedPlatformApiFactory factory) => _factory = factory;

    // ─── GET ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFeature_ShouldReturn200_WithRedactedCredentials_WhenAdminAuthenticated()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/admin/features/agent-assist");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["enabled"]!.GetValue<bool>().Should().BeFalse(
            because: "fresh test host seeds the toggle from empty options → disabled");
        // apiKeyMasked must be null OR "***", never the raw secret
        var masked = json["apiKeyMasked"];
        (masked is null || masked.GetValue<string>() == "***").Should().BeTrue();
    }

    [Fact]
    public async Task GetFeature_ShouldReturn401Or403_WhenNotAuthenticated()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateClient(); // no Authorization header

        var response = await client.GetAsync("/api/v1/admin/features/agent-assist");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetFeature_ShouldReturn403_WhenAgentRoleLacksPermission()
    {
        await using var factory = new NonAdminAuthenticatedApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/admin/features/agent-assist");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "Agent-role users must not reach the runtime feature surface");
    }

    // ─── PUT ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFeature_ShouldReturn200_AndPersist_WhenValidDeepgramPayload()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = new
        {
            enabled = true,
            provider = "deepgram",
            credentials = new { apiKey = "dg_live_1234567890" },
        };

        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["enabled"]!.GetValue<bool>().Should().BeTrue();
        json["provider"]!.GetValue<string>().Should().Be("deepgram");
        json["credentialsConfigured"]!.GetValue<bool>().Should().BeTrue();
        json["apiKeyMasked"]!.GetValue<string>().Should().Be("***");

        // Round-trip: the toggle in DI must reflect the new state
        var toggle = factory.Services.GetRequiredService<IAgentAssistFeatureToggle>();
        var state = await toggle.GetCurrentAsync();
        state.Enabled.Should().BeTrue();
        state.Provider.Should().Be("deepgram");
        state.ProtectedCredentials.Should().NotBeNull();
        state.ProtectedCredentials!["apiKey"].Should().NotBe("dg_live_1234567890",
            because: "credentials must always be encrypted before landing in the toggle");
    }

    [Fact]
    public async Task UpdateFeature_ShouldReturn403_WhenNonAdminCaller()
    {
        await using var factory = new NonAdminAuthenticatedApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = new { enabled = true, provider = "deepgram", credentials = new { apiKey = "x" } };
        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateFeature_ShouldReturn400_WhenProviderNotWhitelisted()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = new { enabled = true, provider = "not-a-real-provider", credentials = new { apiKey = "x" } };
        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateFeature_ShouldReturn400_WhenEnabledWithoutCredentials()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        // Deepgram requires apiKey — missing credentials block must fail validation
        var body = new { enabled = true, provider = "deepgram", credentials = (object?)null };
        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
