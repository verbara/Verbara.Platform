using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// W5 (A7) — supervisor stuck-work tooling. <c>GET /supervisor/conversations/stuck</c>
/// lists conversations actively OWNED by an Offline agent in a failover-work state
/// ({Active, OnHold, Consulting}), including the failover-escalated ones the automatic
/// sweep could not re-queue (marked <c>failoverStuck</c>). <c>POST
/// /supervisor/conversations/{id}/reassign</c> manually transfers a stuck conversation
/// to a queue OR an agent and CLEARS the W5 failover markers so a reassigned-then-re-
/// orphaned conversation gets fresh failover treatment. Both are gated by
/// <c>SupervisorPlus</c> (Admin or Supervisor) + <c>RequireOperationalTenant()</c>.
///
/// Seeding writes Agents and Conversations directly into the wired in-memory stores
/// (mirrors <see cref="AdminForceOfflineEndpointTests"/> + <see cref="DeferredPauseEndpointsTests"/>).
/// The default <see cref="AuthenticatedPlatformApiFactory"/> caller is an Admin user, so
/// it satisfies SupervisorPlus; <see cref="NonAdminAuthenticatedApiFactory"/> (Agent role)
/// is rejected with 403.
/// </summary>
public sealed class SupervisorStuckWorkEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly TenantId s_tenantId = new(AuthenticatedPlatformApiFactory.TestTenantId);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public SupervisorStuckWorkEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // ── GET /supervisor/conversations/stuck ───────────────────────────────────

    [Fact]
    public async Task GetStuckConversations_ShouldReturnConvsOwnedByOfflineAgents_WhenStuck()
    {
        var offlineAgent = await SeedAgentAsync(AgentState.Offline, "Offline Owner");
        var availableAgent = await SeedAgentAsync(AgentState.Available, "Available Owner");

        var stuckConv = await SeedConversationAsync(offlineAgent.AgentId, ConversationState.Active);
        await SeedConversationAsync(availableAgent.AgentId, ConversationState.Active); // not stuck — owner is Available

        var stuck = await GetStuckAsync();

        stuck.Should().ContainSingle(c => c.ConversationId == stuckConv.ConversationId.Value);
        var dto = stuck.Single(c => c.ConversationId == stuckConv.ConversationId.Value);
        dto.OwnerAgentId.Should().Be(offlineAgent.AgentId.Value);
        dto.OwnerAgentName.Should().Be("Offline Owner");
        dto.OwnerOfflineSince.Should().NotBeNull();
        dto.State.Should().Be("Active");
        dto.Escalated.Should().BeFalse();
        stuck.Should().NotContain(c => c.OwnerAgentId == availableAgent.AgentId.Value);
    }

    [Fact]
    public async Task GetStuckConversations_ShouldFlagEscalated_WhenFailoverStuckSet()
    {
        var offlineAgent = await SeedAgentAsync(AgentState.Offline, "Escalated Owner");
        var conv = await SeedConversationAsync(
            offlineAgent.AgentId,
            ConversationState.Active,
            metadata: new Dictionary<string, string>
            {
                ["failoverStuck"] = "true",
                ["failoverAttempts"] = "3",
            });

        var stuck = await GetStuckAsync();

        var dto = stuck.Single(c => c.ConversationId == conv.ConversationId.Value);
        dto.Escalated.Should().BeTrue();
        dto.FailoverAttempts.Should().Be(3);
    }

    [Fact]
    public async Task GetStuckConversations_ShouldReturnEmpty_WhenNoStuckWork()
    {
        // A fresh tenant with no offline-owned work → an empty list (other tenants' /
        // other tests' seeds are tenant-scoped out, but this asserts the no-match path:
        // an Available agent owning Active work is never stuck).
        var availableAgent = await SeedAgentAsync(AgentState.Available, "Busy-but-online");
        await SeedConversationAsync(availableAgent.AgentId, ConversationState.Active);

        var stuck = await GetStuckAsync();

        stuck.Should().NotContain(c => c.OwnerAgentId == availableAgent.AgentId.Value);
    }

    // ── POST /supervisor/conversations/{id}/reassign ──────────────────────────

    [Fact]
    public async Task ReassignConversation_ShouldTransferToQueue_WhenTargetQueueProvided()
    {
        var offlineAgent = await SeedAgentAsync(AgentState.Offline, "Owner Q");
        var conv = await SeedConversationAsync(offlineAgent.AgentId, ConversationState.Active);
        var queueId = EntityId.New();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conv.ConversationId.Value}/reassign",
            new { targetQueueId = queueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var updated = await GetConversationAsync(conv.ConversationId);
        updated.State.Should().Be(ConversationState.Queued);
        updated.Owner!.Kind.Should().Be(ConversationOwnerKind.Queue);
        updated.Owner.OwnerId.Should().Be(queueId);
    }

    [Fact]
    public async Task ReassignConversation_ShouldTransferToAgent_WhenTargetAgentProvided()
    {
        var offlineAgent = await SeedAgentAsync(AgentState.Offline, "Owner A");
        var conv = await SeedConversationAsync(offlineAgent.AgentId, ConversationState.Active);
        var targetAgentId = EntityId.New();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conv.ConversationId.Value}/reassign",
            new { targetAgentId = targetAgentId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var updated = await GetConversationAsync(conv.ConversationId);
        updated.State.Should().Be(ConversationState.Active);
        updated.Owner!.Kind.Should().Be(ConversationOwnerKind.Agent);
        updated.Owner.OwnerId.Should().Be(targetAgentId);
    }

    [Fact]
    public async Task ReassignConversation_ShouldClearFailoverMarkers_WhenReassigned()
    {
        var offlineAgent = await SeedAgentAsync(AgentState.Offline, "Owner Stuck");
        var conv = await SeedConversationAsync(
            offlineAgent.AgentId,
            ConversationState.Active,
            metadata: new Dictionary<string, string>
            {
                ["failoverAttempts"] = "2",
                ["failoverStuck"] = "true",
                ["originQueueId"] = "queue-origin",
            });

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conv.ConversationId.Value}/reassign",
            new { targetQueueId = EntityId.New().Value });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var updated = await GetConversationAsync(conv.ConversationId);
        updated.Metadata.ContainsKey("failoverAttempts").Should().BeFalse();
        updated.Metadata.ContainsKey("failoverStuck").Should().BeFalse();
        // Unrelated markers are left intact (only the W5 failover markers are cleared).
        updated.Metadata.ContainsKey("originQueueId").Should().BeTrue();
    }

    [Fact]
    public async Task ReassignConversation_ShouldEmitAuditEvent_WhenReassigned()
    {
        var offlineAgent = await SeedAgentAsync(AgentState.Offline, "Owner Audit");
        var conv = await SeedConversationAsync(offlineAgent.AgentId, ConversationState.Active);
        var queueId = EntityId.New();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conv.ConversationId.Value}/reassign",
            new { targetQueueId = queueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var auditStore = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await auditStore.SearchAsync(
            s_tenantId,
            new AuditQuery(Action: "conversation.reassigned", Page: 1, PageSize: 100),
            CancellationToken.None);

        hits.Items.Should().ContainSingle(e =>
            e.TargetId == conv.ConversationId.Value
            && e.Category == "conversations"
            && e.ActorType == "user"
            && e.Metadata != null
            && e.Metadata["target_queue"] == queueId.Value);
    }

    [Fact]
    public async Task ReassignConversation_ShouldReturnBadRequest_WhenBothTargetsProvided()
    {
        var offlineAgent = await SeedAgentAsync(AgentState.Offline, "Owner Both");
        var conv = await SeedConversationAsync(offlineAgent.AgentId, ConversationState.Active);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conv.ConversationId.Value}/reassign",
            new { targetQueueId = "q1", targetAgentId = "a1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReassignConversation_ShouldReturnBadRequest_WhenNeitherTargetProvided()
    {
        var offlineAgent = await SeedAgentAsync(AgentState.Offline, "Owner Neither");
        var conv = await SeedConversationAsync(offlineAgent.AgentId, ConversationState.Active);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conv.ConversationId.Value}/reassign",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReassignConversation_ShouldReturnNotFound_WhenConversationMissing()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/supervisor/conversations/no-such-conversation/reassign",
            new { targetQueueId = EntityId.New().Value });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReassignConversation_ShouldReturnForbidden_WhenCallerNotSupervisor()
    {
        // SupervisorPlus is ROLE-driven (Admin or Supervisor): an Agent-role caller is
        // rejected by the auth pipeline (403) before the handler runs.
        await using var nonSupervisor = new NonAdminAuthenticatedApiFactory();
        using var nonSupervisorClient = nonSupervisor.CreateAuthenticatedClient();

        var response = await nonSupervisorClient.PostAsJsonAsync(
            "/api/v1/supervisor/conversations/any-conversation/reassign",
            new { targetQueueId = "q1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Seeding / inspection helpers ──────────────────────────────────────────

    private async Task<List<StuckDto>> GetStuckAsync()
    {
        var response = await _client.GetAsync("/api/v1/supervisor/conversations/stuck");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<List<StuckDto>>(s_json);
        return list ?? [];
    }

    private async Task<Agent> SeedAgentAsync(AgentState state, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
        var agent = new Agent
        {
            AgentId = EntityId.New(),
            TenantId = s_tenantId,
            UserId = EntityId.New(),
            DisplayName = displayName,
            State = state,
            OfflineSince = state == AgentState.Offline ? DateTimeOffset.UtcNow.AddMinutes(-5) : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(agent, CancellationToken.None);
        return agent;
    }

    private async Task<Conversation> SeedConversationAsync(
        EntityId ownerAgentId,
        ConversationState state,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conv = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = s_tenantId,
            ContactId = EntityId.New(),
            Channel = ChannelType.WebChat,
            State = state,
            Owner = ConversationOwner.ForAgent(ownerAgentId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (metadata is not null)
            foreach (var (k, v) in metadata)
                conv.SetMetadata(k, v);
        await store.SaveAsync(conv, CancellationToken.None);
        return conv;
    }

    private async Task<Conversation> GetConversationAsync(EntityId conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conv = await store.GetByIdAsync(s_tenantId, conversationId, CancellationToken.None);
        conv.Should().NotBeNull();
        return conv!;
    }

    // Local mirror of the wire DTO (the source DTO is internal to the Api project).
    private sealed record StuckDto(
        string ConversationId,
        string Channel,
        string State,
        string OwnerAgentId,
        string OwnerAgentName,
        DateTimeOffset? OwnerOfflineSince,
        int FailoverAttempts,
        bool Escalated);
}
