using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Phase 3A·C — the browser softphone needs the owning agent's own SIP
/// credentials, so <c>GET /agents/me</c> deliberately surfaces
/// <c>extension</c> + <c>sipPassword</c> — but ONLY for the caller's own
/// agent record, and the admin agent endpoints must NEVER echo the secret
/// (closing a pre-existing leak where the raw <see cref="Agent"/> entity was
/// serialized with its plaintext <c>SipPassword</c>).
/// </summary>
public sealed class AgentMeSipExposureTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public AgentMeSipExposureTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetCurrentAgent_ShouldReturnExtensionAndSipPassword_WhenRequestedByOwningAgent()
    {
        await SeedAgentForCallerAsync(extension: "1001", sipPassword: "s3cr3t-abc");

        var response = await _client.GetAsync("/api/v1/agents/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<AgentMeDto>(s_json);
        dto.Should().NotBeNull();
        dto!.Extension.Should().Be("1001");
        dto.SipPassword.Should().Be("s3cr3t-abc");
    }

    [Fact]
    public async Task GetCurrentAgent_ShouldReturnAutoAnswer_WhenSet()
    {
        // 3B.2b: the softphone reads its own auto-answer override from /agents/me.
        await SeedAgentForCallerAsync(extension: "1003", sipPassword: "p", autoAnswer: true);

        var response = await _client.GetAsync("/api/v1/agents/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<AgentMeDto>(s_json);
        dto!.AutoAnswer.Should().BeTrue();
    }

    [Fact]
    public async Task GetCurrentAgent_ShouldNeverReturnAnotherAgentsSecret_WhenResolvedFromJwtSub()
    {
        // Caller's own agent — its secret is allowed in the response.
        await SeedAgentForCallerAsync(extension: "1002", sipPassword: "mine-pass");
        // A different agent (different user) whose secret must never surface here.
        await SeedAgentForUserAsync(
            userId: EntityId.New(),
            extension: "9999",
            sipPassword: "other-secret-canary");

        var response = await _client.GetAsync("/api/v1/agents/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("other-secret-canary");

        var dto = JsonSerializer.Deserialize<AgentMeDto>(body, s_json);
        dto!.SipPassword.Should().Be("mine-pass");
    }

    [Fact]
    public async Task AgentMe_ShouldReturnEffectiveCapacity_WhenOverrideAndDefault()
    {
        // W6-A6 — /agents/me must surface the RESOLVER-computed effective capacity
        // (tenant default merged with the per-agent override, MaxVoice pinned to 1),
        // NOT the A1 temp bridge over the hard ChannelCapacity() defaults.
        await SeedAgentForCallerWithCapacityAsync(maxChat: 8);

        var response = await _client.GetAsync("/api/v1/agents/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<AgentMeCapacityDto>(s_json);
        dto.Should().NotBeNull();
        // Chat overridden to 8 wins over the tenant default; voice stays pinned to 1.
        dto!.Capacity!.MaxChat.Should().Be(8);
        dto.Capacity.MaxVoice.Should().Be(1);
        // Non-overridden channels fall back to the tenant default (3/5/3/5 for the test tenant).
        dto.Capacity.MaxEmail.Should().Be(5);
    }

    [Fact]
    public async Task ListAgents_ShouldNotIncludeSipPassword_InResponseJson()
    {
        await SeedAgentForUserAsync(
            userId: EntityId.New(),
            extension: "2001",
            sipPassword: "leak-canary-list");

        var response = await _client.GetAsync("/api/v1/admin/agents");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("leak-canary-list");
        body.ToLowerInvariant().Should().NotContain("sippassword");
    }

    [Fact]
    public async Task GetAgentById_ShouldNotIncludeSipPassword()
    {
        var agentId = await SeedAgentForUserAsync(
            userId: EntityId.New(),
            extension: "2002",
            sipPassword: "leak-canary-byid");

        var response = await _client.GetAsync($"/api/v1/admin/agents/{agentId.Value}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("leak-canary-byid");
        body.ToLowerInvariant().Should().NotContain("sippassword");
    }

    // ── Seeding helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Seeds an agent owned by the authenticated caller. Removes any prior
    /// caller-owned agent first so <c>GetByUserIdAsync</c> stays deterministic
    /// across tests sharing the class fixture's in-memory store.
    /// </summary>
    private async Task SeedAgentForCallerAsync(string extension, string sipPassword, bool? autoAnswer = null)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
        var tenantId = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId);
        var callerUserId = EntityId.From(AuthenticatedPlatformApiFactory.TestUserId);

        var existing = await store.GetByUserIdAsync(tenantId, callerUserId, CancellationToken.None);
        if (existing is not null)
            await store.DeleteAsync(tenantId, existing.AgentId, CancellationToken.None);

        await store.SaveAsync(NewAgent(tenantId, callerUserId, extension, sipPassword, autoAnswer), CancellationToken.None);
    }

    /// <summary>
    /// Seeds a caller-owned agent carrying a per-agent capacity override (W6-A6).
    /// Replaces any prior caller-owned agent so GetByUserIdAsync stays deterministic.
    /// </summary>
    private async Task SeedAgentForCallerWithCapacityAsync(int maxChat)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
        var tenantId = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId);
        var callerUserId = EntityId.From(AuthenticatedPlatformApiFactory.TestUserId);

        var existing = await store.GetByUserIdAsync(tenantId, callerUserId, CancellationToken.None);
        if (existing is not null)
            await store.DeleteAsync(tenantId, existing.AgentId, CancellationToken.None);

        var agent = NewAgent(tenantId, callerUserId, "1500", "p");
        agent.CapacityOverride = new ChannelCapacityOverride { MaxChat = maxChat };
        await store.SaveAsync(agent, CancellationToken.None);
    }

    private async Task<EntityId> SeedAgentForUserAsync(EntityId userId, string extension, string sipPassword)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
        var tenantId = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId);
        var agent = NewAgent(tenantId, userId, extension, sipPassword);
        await store.SaveAsync(agent, CancellationToken.None);
        return agent.AgentId;
    }

    private static Agent NewAgent(TenantId tenantId, EntityId userId, string extension, string sipPassword, bool? autoAnswer = null) => new()
    {
        AgentId = EntityId.New(),
        TenantId = tenantId,
        UserId = userId,
        DisplayName = "SIP Test Agent",
        State = AgentState.Offline,
        Extension = extension,
        SipPassword = sipPassword,
        AutoAnswer = autoAnswer,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed record AgentMeDto(string? Extension, string? SipPassword, bool? AutoAnswer);

    private sealed record AgentMeCapacityDto(CapacityShape? Capacity);

    private sealed record CapacityShape(int MaxVoice, int MaxChat, int MaxEmail, int MaxSms, int MaxTotal);
}
