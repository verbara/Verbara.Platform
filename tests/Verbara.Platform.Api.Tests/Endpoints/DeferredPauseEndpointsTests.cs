using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// W4 (A5) — deferred-pause PRODUCER endpoints. <c>PUT /agents/me/state</c> is
/// pending-aware (a deferrable aux state requested while the agent still owns
/// active work is RECORDED as a PendingState rather than applied); plus
/// <c>POST /agents/me/pause/cancel</c> (drop the pending pause, stay routable)
/// and <c>POST /agents/me/pause/force</c> (apply it now, skip the drain wait).
/// <c>GET /agents/me</c> exposes the pending fields + an active-work count.
///
/// Seeding mirrors <c>AgentMeSipExposureTests</c>: the test API key maps to
/// <see cref="AuthenticatedPlatformApiFactory.TestUserId"/>, so an agent owned by
/// that user is resolved by <c>GetByUserIdAsync</c>. Active work is seeded by
/// saving a Conversation in <c>Active</c> state owned by that agent into the wired
/// in-memory <c>IConversationStore</c> — which is what <c>CountActiveWorkAsync</c>
/// counts. The critical SaveAsync-before-publish ordering is asserted by reading
/// the persisted agent back from the store after the call.
/// </summary>
public sealed class DeferredPauseEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly TenantId s_tenantId = new(AuthenticatedPlatformApiFactory.TestTenantId);
    private static readonly EntityId s_callerUserId = EntityId.From(AuthenticatedPlatformApiFactory.TestUserId);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public DeferredPauseEndpointsTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // ── PUT /me/state — pending-aware ─────────────────────────────────────────

    [Fact]
    public async Task UpdateAgentState_ShouldSetPending_WhenDeferrableRequestedWithActiveWork()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);
        await SeedActiveWorkAsync(agentId);

        var (captured, sub) = SubscribeEvents();
        using var _ = sub;

        var response = await _client.PutAsync(
            "/api/v1/agents/me/state", JsonContent.Create(new { state = "Break" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<AgentMeDto>(s_json);
        dto!.State.Should().Be("Available");
        dto.PendingState.Should().Be("Break");
        dto.ActiveWorkCount.Should().Be(1);

        var agent = await GetAgentAsync(agentId);
        agent.State.Should().Be(AgentState.Available);
        agent.PendingState.Should().Be(AgentState.Break);

        captured.OfType<AgentPendingStateChangedEvent>()
            .Should().ContainSingle(e => e.AgentId == agentId.Value && e.PendingState == "Break");
    }

    [Fact]
    public async Task UpdateAgentState_ShouldApplyImmediately_WhenDeferrableRequestedWithNoActiveWork()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);
        await ClearWorkAsync(agentId);

        var (captured, sub) = SubscribeEvents();
        using var _ = sub;

        var response = await _client.PutAsync(
            "/api/v1/agents/me/state", JsonContent.Create(new { state = "Break" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<AgentMeDto>(s_json);
        dto!.State.Should().Be("Break");
        dto.PendingState.Should().BeNull();

        var agent = await GetAgentAsync(agentId);
        agent.State.Should().Be(AgentState.Break);
        agent.HasPendingPause.Should().BeFalse();

        captured.OfType<AgentStateChangedEvent>()
            .Should().ContainSingle(e => e.AgentId == agentId.Value && e.NewState == "Break");
        captured.OfType<AgentPendingStateChangedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAgentState_ShouldPersistPendingBeforePublishing_WhenSettingPending()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);
        await SeedActiveWorkAsync(agentId);

        var response = await _client.PutAsync(
            "/api/v1/agents/me/state", JsonContent.Create(new { state = "Lunch" }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // After the call the store row already carries the pending target — proving
        // SaveAsync ran before the event was published (the A4 ordering contract).
        var agent = await GetAgentAsync(agentId);
        agent.PendingState.Should().Be(AgentState.Lunch);
        agent.HasPendingPause.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAgentState_ShouldUpdatePendingTarget_WhenReRequestedWhilePending()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);
        await SeedActiveWorkAsync(agentId);

        // First request → pending Break.
        var first = await _client.PutAsync(
            "/api/v1/agents/me/state", JsonContent.Create(new { state = "Break" }));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var (captured, sub) = SubscribeEvents();
        using var _ = sub;

        // Re-request a DIFFERENT deferrable target → overwrite the pending target.
        var second = await _client.PutAsync(
            "/api/v1/agents/me/state", JsonContent.Create(new { state = "Lunch" }));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await second.Content.ReadFromJsonAsync<AgentMeDto>(s_json);
        dto!.State.Should().Be("Available");
        dto.PendingState.Should().Be("Lunch");

        var agent = await GetAgentAsync(agentId);
        agent.PendingState.Should().Be(AgentState.Lunch);

        captured.OfType<AgentPendingStateChangedEvent>()
            .Should().ContainSingle(e => e.AgentId == agentId.Value && e.PendingState == "Lunch");
    }

    [Fact]
    public async Task UpdateAgentState_ShouldCancelPendingAndApply_WhenRoutableRequestedWhilePending()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Busy);
        await SeedActiveWorkAsync(agentId);

        // Set a pending pause first (Busy + active work → pending Break).
        var setup = await _client.PutAsync(
            "/api/v1/agents/me/state", JsonContent.Create(new { state = "Break" }));
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAgentAsync(agentId)).HasPendingPause.Should().BeTrue();

        var (captured, sub) = SubscribeEvents();
        using var _ = sub;

        // Routable target while pending → cancel pending AND apply Available.
        var response = await _client.PutAsync(
            "/api/v1/agents/me/state", JsonContent.Create(new { state = "Available" }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<AgentMeDto>(s_json);
        dto!.State.Should().Be("Available");
        dto.PendingState.Should().BeNull();

        var agent = await GetAgentAsync(agentId);
        agent.State.Should().Be(AgentState.Available);
        agent.HasPendingPause.Should().BeFalse();

        captured.OfType<AgentPendingStateChangedEvent>()
            .Should().ContainSingle(e => e.AgentId == agentId.Value && e.PendingState == null);
        captured.OfType<AgentStateChangedEvent>()
            .Should().ContainSingle(e => e.AgentId == agentId.Value && e.NewState == "Available");
    }

    // ── POST /me/pause/cancel ─────────────────────────────────────────────────

    [Fact]
    public async Task CancelPendingPause_ShouldClearPendingAndStayRoutable_WhenPending()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);
        await SeedActiveWorkAsync(agentId);
        await _client.PutAsync("/api/v1/agents/me/state", JsonContent.Create(new { state = "Break" }));
        (await GetAgentAsync(agentId)).HasPendingPause.Should().BeTrue();

        var (captured, sub) = SubscribeEvents();
        using var _ = sub;

        var response = await _client.PostAsync("/api/v1/agents/me/pause/cancel", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<AgentMeDto>(s_json);
        dto!.PendingState.Should().BeNull();
        dto.State.Should().Be("Available");

        var agent = await GetAgentAsync(agentId);
        agent.HasPendingPause.Should().BeFalse();
        agent.State.Should().Be(AgentState.Available);

        captured.OfType<AgentPendingStateChangedEvent>()
            .Should().ContainSingle(e => e.AgentId == agentId.Value && e.PendingState == null);
    }

    [Fact]
    public async Task CancelPendingPause_ShouldReturnOkNoOp_WhenNotPending()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);

        var (captured, sub) = SubscribeEvents();
        using var _ = sub;

        var response = await _client.PostAsync("/api/v1/agents/me/pause/cancel", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetAgentAsync(agentId)).HasPendingPause.Should().BeFalse();
        captured.OfType<AgentPendingStateChangedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CancelPendingPause_ShouldReturnNotFound_WhenUserHasNoAgent()
    {
        await RemoveCallerAgentAsync();

        var response = await _client.PostAsync("/api/v1/agents/me/pause/cancel", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /me/pause/force ──────────────────────────────────────────────────

    [Fact]
    public async Task ForcePendingPause_ShouldApplyStateImmediately_WhenPending()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);
        await SeedActiveWorkAsync(agentId);
        await _client.PutAsync("/api/v1/agents/me/state", JsonContent.Create(new { state = "Lunch" }));
        (await GetAgentAsync(agentId)).HasPendingPause.Should().BeTrue();

        var (captured, sub) = SubscribeEvents();
        using var _ = sub;

        var response = await _client.PostAsync("/api/v1/agents/me/pause/force", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<AgentMeDto>(s_json);
        dto!.State.Should().Be("Lunch");
        dto.PendingState.Should().BeNull();

        var agent = await GetAgentAsync(agentId);
        agent.State.Should().Be(AgentState.Lunch);
        agent.HasPendingPause.Should().BeFalse();

        // Force publishes ONLY AgentStateChangedEvent (no unpause→repause flicker).
        captured.OfType<AgentStateChangedEvent>()
            .Should().ContainSingle(e => e.AgentId == agentId.Value && e.NewState == "Lunch");
        captured.OfType<AgentPendingStateChangedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task ForcePendingPause_ShouldReturnOkNoOp_WhenNotPending()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);

        var (captured, sub) = SubscribeEvents();
        using var _ = sub;

        var response = await _client.PostAsync("/api/v1/agents/me/pause/force", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var agent = await GetAgentAsync(agentId);
        agent.State.Should().Be(AgentState.Available);
        agent.HasPendingPause.Should().BeFalse();
        captured.OfType<AgentStateChangedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task ForcePendingPause_ShouldReturnNotFound_WhenUserHasNoAgent()
    {
        await RemoveCallerAgentAsync();

        var response = await _client.PostAsync("/api/v1/agents/me/pause/force", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /me ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentAgent_ShouldIncludePendingAndActiveWorkCount_WhenPending()
    {
        var agentId = await SeedAgentForCallerAsync(AgentState.Available);
        await SeedActiveWorkAsync(agentId);
        await _client.PutAsync("/api/v1/agents/me/state", JsonContent.Create(new { state = "Break" }));

        var response = await _client.GetAsync("/api/v1/agents/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<AgentMeDto>(s_json);
        dto!.State.Should().Be("Available");
        dto.PendingState.Should().Be("Break");
        dto.ActiveWorkCount.Should().Be(1);
        dto.PendingSince.Should().NotBeNull();
    }

    // ── Seeding / inspection helpers ──────────────────────────────────────────

    private (List<PlatformEvent> Captured, IDisposable Subscription) SubscribeEvents()
    {
        var eventBus = _factory.Services.GetRequiredService<PlatformEventBus>();
        var captured = new List<PlatformEvent>();
        var sub = eventBus.Events.Subscribe(captured.Add);
        return (captured, sub);
    }

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
            DisplayName = "Deferred Pause Test Agent",
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

    /// <summary>
    /// Saves a single <c>Active</c> conversation OWNED by the agent so that
    /// <c>CountActiveWorkAsync</c> returns 1 (the deferred-pause gate). Clears any
    /// prior caller work first so the count is deterministic across tests.
    /// </summary>
    private async Task SeedActiveWorkAsync(EntityId agentId)
    {
        await ClearWorkAsync(agentId);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = s_tenantId,
            ContactId = EntityId.New(),
            Channel = ChannelType.WebChat,
            Owner = ConversationOwner.ForAgent(agentId),
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(conversation, CancellationToken.None);
    }

    /// <summary>
    /// Drives every active-work conversation owned by the agent to a terminal state so
    /// <c>CountActiveWorkAsync</c> returns 0 — the in-memory store has no delete-by-agent,
    /// so we close them out through the valid transition path to <c>WrapUp → Closed</c>.
    /// </summary>
    private async Task ClearWorkAsync(EntityId agentId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();

        foreach (var state in ConversationStateMachine.ActiveWorkStates)
        {
            var rows = await store.ListByStateAsync(s_tenantId, state, 1000, CancellationToken.None);
            foreach (var c in rows.Where(c =>
                c.Owner is { Kind: ConversationOwnerKind.Agent } o && o.OwnerId == agentId))
            {
                // OnHold/Consulting → Active → WrapUp → Closed; Active → WrapUp → Closed;
                // WrapUp → Closed. Walk the transition table from the current state.
                if (c.State is ConversationState.OnHold or ConversationState.Consulting)
                    c.TransitionTo(ConversationState.Active);
                if (c.State == ConversationState.Active)
                    c.TransitionTo(ConversationState.WrapUp);
                if (c.State == ConversationState.WrapUp)
                    c.TransitionTo(ConversationState.Closed);
                await store.SaveAsync(c, CancellationToken.None);
            }
        }
    }

    private sealed record AgentMeDto(
        string State,
        string? PendingState,
        string? PendingReason,
        DateTimeOffset? PendingSince,
        int ActiveWorkCount);
}
