using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// W3 (A6) — admin force-offline lever. <c>POST /admin/agents/{id}/force-offline</c>
/// is the MANUAL supervisor counterpart to the automatic heartbeat/reaper liveness
/// flow: it forces a routing-zombie / wedged agent Offline, removes its presence
/// key, (only on a real transition) publishes an <see cref="AgentStateChangedEvent"/>
/// so RealtimeStateBridge pauses the agent in Asterisk, optionally revokes the
/// owning user's refresh-token sessions, and writes a best-effort audit entry.
/// Gated by <c>AdminOnly</c> + <c>RequireOperationalTenant()</c> only (matches the
/// sibling /admin/agents/* endpoints — no granular permission check).
/// </summary>
public sealed class AdminForceOfflineEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly TenantId s_tenantId = new(AuthenticatedPlatformApiFactory.TestTenantId);

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public AdminForceOfflineEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task ForceAgentOffline_ShouldSetAgentOffline_WhenAdminCalls()
    {
        var (agentId, _) = await SeedAgentAsync(AgentState.Available);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{agentId.Value}/force-offline",
            new { revokeSessions = false });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetAgentAsync(agentId)).State.Should().Be(AgentState.Offline);
    }

    [Fact]
    public async Task ForceAgentOffline_ShouldPublishAgentStateChangedEvent_WhenAgentRoutable()
    {
        var (agentId, _) = await SeedAgentAsync(AgentState.Available);

        var eventBus = _factory.Services.GetRequiredService<PlatformEventBus>();
        var captured = new List<PlatformEvent>();
        using var _sub = eventBus.Events.Subscribe(captured.Add);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{agentId.Value}/force-offline",
            new { revokeSessions = false });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        captured.OfType<AgentStateChangedEvent>()
            .Should().ContainSingle(e =>
                e.AgentId == agentId.Value
                && e.NewState == AgentState.Offline.ToString());
    }

    [Fact]
    public async Task ForceAgentOffline_ShouldNotPublishEvent_WhenAgentAlreadyOffline()
    {
        var (agentId, _) = await SeedAgentAsync(AgentState.Offline);

        var eventBus = _factory.Services.GetRequiredService<PlatformEventBus>();
        var captured = new List<PlatformEvent>();
        using var _sub = eventBus.Events.Subscribe(captured.Add);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{agentId.Value}/force-offline",
            new { revokeSessions = false });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        captured.OfType<AgentStateChangedEvent>()
            .Where(e => e.AgentId == agentId.Value)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ForceAgentOffline_ShouldRemovePresenceKey_WhenCalled()
    {
        var (agentId, _) = await SeedAgentAsync(AgentState.Available);
        await TouchPresenceKeyAsync(agentId);
        (await IsAliveAsync(agentId)).Should().BeTrue();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{agentId.Value}/force-offline",
            new { revokeSessions = false });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await IsAliveAsync(agentId)).Should().BeFalse();
    }

    [Fact]
    public async Task ForceAgentOffline_ShouldReturnNotFound_WhenAgentMissing()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/agents/no-such-agent/force-offline",
            new { revokeSessions = false });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ForceAgentOffline_ShouldRevokeRefreshTokens_WhenRevokeSessionsTrue()
    {
        var (agentId, ownerUserId) = await SeedAgentAsync(AgentState.Available);
        var tokenId = await SeedRefreshTokenAsync(ownerUserId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{agentId.Value}/force-offline",
            new { revokeSessions = true });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var token = await GetRefreshTokenAsync(tokenId);
        token!.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task ForceAgentOffline_ShouldNotRevokeTokens_WhenRevokeSessionsFalse()
    {
        var (agentId, ownerUserId) = await SeedAgentAsync(AgentState.Available);
        var tokenId = await SeedRefreshTokenAsync(ownerUserId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{agentId.Value}/force-offline",
            new { revokeSessions = false });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var token = await GetRefreshTokenAsync(tokenId);
        token!.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task ForceAgentOffline_ShouldEmitAuditEvent_WhenCalled()
    {
        var (agentId, _) = await SeedAgentAsync(AgentState.Busy);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{agentId.Value}/force-offline",
            new { revokeSessions = true });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var auditStore = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await auditStore.SearchAsync(
            s_tenantId,
            new AuditQuery(Action: "agent.force_offline", Page: 1, PageSize: 100),
            CancellationToken.None);

        hits.Items.Should().ContainSingle(e =>
            e.TargetId == agentId.Value
            && e.Category == "queues"
            && e.ActorType == "user"
            && e.Metadata != null
            && e.Metadata["old_state"] == "Busy"
            && e.Metadata["revoked_sessions"] == "true");
    }

    [Fact]
    public async Task ForceAgentOffline_ShouldReturnForbidden_WhenCallerNotAdmin()
    {
        // Seed the target agent in the admin tenant first (so the call would
        // succeed for an admin), then attempt it from a non-admin caller in a
        // DIFFERENT tenant — the AdminOnly policy must reject before any work.
        await using var nonAdmin = new NonAdminAuthenticatedApiFactory();
        using var nonAdminClient = nonAdmin.CreateAuthenticatedClient();

        var response = await nonAdminClient.PostAsJsonAsync(
            "/api/v1/admin/agents/any-agent/force-offline",
            new { revokeSessions = false });

        // AdminOnly refuses the Agent-role caller → 403 from the auth pipeline.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Seeding / inspection helpers ──────────────────────────────────────────

    /// <summary>
    /// Seeds a fresh agent owned by a NEW random user (not the caller) in the
    /// given state. Returns the agent id and its owning user id so refresh-token
    /// revocation can be asserted against the correct user.
    /// </summary>
    private async Task<(EntityId AgentId, EntityId OwnerUserId)> SeedAgentAsync(AgentState state)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();

        var ownerUserId = EntityId.New();
        var agent = new Agent
        {
            AgentId = EntityId.New(),
            TenantId = s_tenantId,
            UserId = ownerUserId,
            DisplayName = "Force-Offline Target",
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(agent, CancellationToken.None);
        return (agent.AgentId, ownerUserId);
    }

    private async Task<Agent> GetAgentAsync(EntityId agentId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentStore>();
        var agent = await store.GetByIdAsync(s_tenantId, agentId, CancellationToken.None);
        agent.Should().NotBeNull();
        return agent!;
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

    private async Task<string> SeedRefreshTokenAsync(EntityId ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        var tokenId = $"rt-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(
            new RefreshToken
            {
                TokenId = tokenId,
                UserId = ownerUserId.Value,
                TenantId = s_tenantId.Value,
                TokenHash = $"hash-{tokenId}",
                ExpiresAt = now.AddDays(1),
                CreatedAt = now,
                LastActivityAt = now,
            },
            CancellationToken.None);
        return tokenId;
    }

    private async Task<RefreshToken?> GetRefreshTokenAsync(string tokenId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        return await store.GetByTokenIdAsync(tokenId, CancellationToken.None);
    }
}
