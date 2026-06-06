using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// W3 (A3 + A4) — agent self-liveness endpoints. <c>POST /agents/me/heartbeat</c>
/// is a pure proof-of-life touch (no state change); <c>POST /agents/me/offline</c>
/// is the graceful-departure / beacon target that forces the agent Offline,
/// removes its presence key, and (only on a real transition) publishes an
/// <see cref="AgentStateChangedEvent"/> so RealtimeStateBridge pauses the agent
/// in Asterisk. Mirrors the seeding pattern of <c>AgentMeSipExposureTests</c>:
/// the test API key maps to <see cref="AuthenticatedPlatformApiFactory.TestUserId"/>,
/// so an agent owned by that user is resolved by <c>GetByUserIdAsync</c>.
/// </summary>
public sealed class AgentLivenessEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly TenantId s_tenantId = new(AuthenticatedPlatformApiFactory.TestTenantId);
    private static readonly EntityId s_callerUserId = EntityId.From(AuthenticatedPlatformApiFactory.TestUserId);

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public AgentLivenessEndpointsTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // ── Heartbeat (A3) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_ShouldReturnNoContent_WhenAuthenticatedAgent()
    {
        await SeedTimeoutAsync(60);
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);

        var response = await _client.PostAsync("/api/v1/agents/me/heartbeat", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _ = agentId;
    }

    [Fact]
    public async Task Heartbeat_ShouldMakeAgentAlive_WhenCalled()
    {
        await SeedTimeoutAsync(60);
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);

        var response = await _client.PostAsync("/api/v1/agents/me/heartbeat", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await IsAliveAsync(agentId)).Should().BeTrue();
    }

    [Fact]
    public async Task Heartbeat_ShouldReturnNotFound_WhenUserHasNoAgent()
    {
        await RemoveCallerAgentAsync();

        var response = await _client.PostAsync("/api/v1/agents/me/heartbeat", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Heartbeat_ShouldNotChangeAgentState_WhenAgentBusy()
    {
        await SeedTimeoutAsync(60);
        var agentId = await SeedAgentForCallerAsync(AgentState.Busy);

        var response = await _client.PostAsync("/api/v1/agents/me/heartbeat", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var agent = await GetAgentAsync(agentId);
        agent.State.Should().Be(AgentState.Busy);
    }

    [Fact]
    public async Task Heartbeat_ShouldNotWriteKey_WhenTenantTimeoutIsZero()
    {
        await SeedTimeoutAsync(0);
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);
        await RemovePresenceKeyAsync(agentId);

        var response = await _client.PostAsync("/api/v1/agents/me/heartbeat", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await IsAliveAsync(agentId)).Should().BeFalse();
    }

    // ── Graceful offline (A4) ────────────────────────────────────────────────

    [Fact]
    public async Task GoOffline_ShouldSetAgentOffline_WhenAgentRoutable()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);

        var response = await _client.PostAsync("/api/v1/agents/me/offline", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var agent = await GetAgentAsync(agentId);
        agent.State.Should().Be(AgentState.Offline);
    }

    [Fact]
    public async Task GoOffline_ShouldPublishAgentStateChangedEvent_WhenTransitioningFromRoutable()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);

        var eventBus = _factory.Services.GetRequiredService<PlatformEventBus>();
        var captured = new List<PlatformEvent>();
        using var _sub = eventBus.Events.Subscribe(captured.Add);

        var response = await _client.PostAsync("/api/v1/agents/me/offline", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        captured.OfType<AgentStateChangedEvent>()
            .Should().ContainSingle(e =>
                e.AgentId == agentId.Value
                && e.NewState == AgentState.Offline.ToString());
    }

    [Fact]
    public async Task GoOffline_ShouldNotPublishEvent_WhenAlreadyOffline()
    {
        await SeedAgentForCallerAsync(AgentState.Offline);

        var eventBus = _factory.Services.GetRequiredService<PlatformEventBus>();
        var captured = new List<PlatformEvent>();
        using var _sub = eventBus.Events.Subscribe(captured.Add);

        var response = await _client.PostAsync("/api/v1/agents/me/offline", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        captured.OfType<AgentStateChangedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task GoOffline_ShouldRemovePresenceKey_WhenCalled()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);
        await TouchPresenceKeyAsync(agentId);
        (await IsAliveAsync(agentId)).Should().BeTrue();

        var response = await _client.PostAsync("/api/v1/agents/me/offline", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await IsAliveAsync(agentId)).Should().BeFalse();
    }

    [Fact]
    public async Task GoOffline_ShouldReturnNoContent_WhenAlreadyOffline()
    {
        await SeedAgentForCallerAsync(AgentState.Offline);

        var response = await _client.PostAsync("/api/v1/agents/me/offline", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GoOffline_ShouldReturnNotFound_WhenUserHasNoAgent()
    {
        await RemoveCallerAgentAsync();

        var response = await _client.PostAsync("/api/v1/agents/me/offline", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Seeding / inspection helpers ──────────────────────────────────────────

    /// <summary>
    /// Seeds an agent owned by the authenticated caller in the given state,
    /// removing any prior caller-owned agent first so <c>GetByUserIdAsync</c>
    /// stays deterministic across tests sharing the class fixture's store.
    /// </summary>
    private async Task<EntityId> SeedAgentForCallerAsync(AgentState state)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();

        var existing = await store.GetByUserIdAsync(s_tenantId, s_callerUserId, CancellationToken.None);
        if (existing is not null)
            await store.DeleteAsync(s_tenantId, existing.AgentId, CancellationToken.None);

        var agent = new Agent
        {
            AgentId = EntityId.New(),
            TenantId = s_tenantId,
            UserId = s_callerUserId,
            DisplayName = "Liveness Test Agent",
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(agent, CancellationToken.None);
        return agent.AgentId;
    }

    private async Task RemoveCallerAgentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
        var existing = await store.GetByUserIdAsync(s_tenantId, s_callerUserId, CancellationToken.None);
        if (existing is not null)
            await store.DeleteAsync(s_tenantId, existing.AgentId, CancellationToken.None);
    }

    private async Task<Agent> GetAgentAsync(EntityId agentId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
        var agent = await store.GetByIdAsync(s_tenantId, agentId, CancellationToken.None);
        agent.Should().NotBeNull();
        return agent!;
    }

    private async Task SeedTimeoutAsync(int seconds)
    {
        using var scope = _factory.Services.CreateScope();
        var configStore = scope.ServiceProvider.GetRequiredService<ITenantAuthConfigStore>();
        await configStore.SaveAsync(
            new TenantAuthConfig
            {
                TenantId = AuthenticatedPlatformApiFactory.TestTenantId,
                AgentLivenessTimeoutSeconds = seconds,
            },
            CancellationToken.None);
    }

    private async Task<bool> IsAliveAsync(EntityId agentId)
    {
        using var scope = _factory.Services.CreateScope();
        var liveness = scope.ServiceProvider.GetRequiredService<IAgentLivenessStore>();
        return await liveness.IsAliveAsync(s_tenantId, agentId, CancellationToken.None);
    }

    private async Task TouchPresenceKeyAsync(EntityId agentId)
    {
        using var scope = _factory.Services.CreateScope();
        var liveness = scope.ServiceProvider.GetRequiredService<IAgentLivenessStore>();
        await liveness.TouchAsync(
            s_tenantId, agentId, TimeSpan.FromSeconds(60), Environment.MachineName, CancellationToken.None);
    }

    private async Task RemovePresenceKeyAsync(EntityId agentId)
    {
        using var scope = _factory.Services.CreateScope();
        var liveness = scope.ServiceProvider.GetRequiredService<IAgentLivenessStore>();
        await liveness.RemoveAsync(s_tenantId, agentId, CancellationToken.None);
    }
}
