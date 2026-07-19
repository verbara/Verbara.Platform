using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class AgentEndpoints
{
    // W4 — aux states the agent may DEFER ("pause when free"): if the agent still
    // has active work, requesting one of these records a PendingState instead of
    // flipping State immediately, so the live call/chat keeps routing-blocked but
    // is not cut off. Routable + teardown targets (Available/Busy/Offline/ACW) are
    // NOT deferrable — they apply at once.
    private static readonly HashSet<AgentState> DeferrableStates =
        [AgentState.Break, AgentState.Lunch, AgentState.Training, AgentState.DND];

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/agents").RequireAuthorization("Authenticated").RequireOperationalTenant();

        group.MapGet("/me", GetCurrentAgent);
        group.MapPut("/me/state", UpdateAgentState);
        group.MapPost("/me/pause/cancel", CancelPendingPause);
        group.MapPost("/me/pause/force", ForcePendingPause);
        group.MapPost("/me/heartbeat", Heartbeat);
        group.MapPost("/me/offline", GoOffline);
    }

    private static async Task<Results<Ok<AgentMeResponseDto>, NotFound>> GetCurrentAgent(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IConversationStore conversationStore,
        [FromServices] IAgentCapacityResolver capacityResolver,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        var agent = await agentStore.GetByUserIdAsync(tenantId, userId, ct);
        if (agent is null)
            return TypedResults.NotFound();

        return await OkDtoAsync(conversationStore, capacityResolver, tenantId, agent, ct);
    }

    // W4 (A5) — pending-aware state change. A deferrable aux state requested while
    // the agent is still working is RECORDED as a PendingState (the live item keeps
    // going, new work is blocked) rather than applied. A routable/teardown target,
    // or any target with no active work, applies immediately via TransitionTo.
    // CRITICAL ORDERING (A4 review): when setting/clearing a pending pause we
    // ALWAYS SaveAsync (so HasPendingPause is durable for GetAvailableAgentsAsync)
    // BEFORE publishing the event — otherwise the event-driven QueuePause could
    // fire while routing still sees the agent as eligible.
    private static async Task<Results<Ok<AgentMeResponseDto>, NotFound>> UpdateAgentState(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IConversationStore conversationStore,
        [FromServices] IAgentCapacityResolver capacityResolver,
        [FromServices] TimeProvider clock,
        PlatformEventBus eventBus,
        [FromBody] UpdateAgentStateRequest body,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        var agent = await agentStore.GetByUserIdAsync(tenantId, userId, ct);
        if (agent is null)
            return TypedResults.NotFound();

        var target = body.State;
        var oldState = agent.State;

        if (DeferrableStates.Contains(target))
        {
            var activeWork = await conversationStore.CountActiveWorkAsync(tenantId, agent.AgentId, ct);

            if (agent.HasPendingPause || activeWork > 0)
            {
                // Set OR re-request (possibly a different deferrable target): record
                // the pending target + refresh PendingSince. State is UNCHANGED — the
                // agent keeps working the live item. Idempotent on the same target.
                agent.PendingState = target;
                agent.PendingReason = body.Reason;
                agent.PendingSince = clock.GetUtcNow();
                await agentStore.SaveAsync(agent, ct);

                eventBus.Publish(new AgentPendingStateChangedEvent(
                    tenantId.ToString(),
                    agent.AgentId.Value,
                    agent.DisplayName,
                    target.ToString()));

                return await OkDtoAsync(conversationStore, capacityResolver, tenantId, agent, ct);
            }

            // Deferrable but no active work and not already pending → apply now.
            agent.TransitionTo(target, clock.GetUtcNow());
            await agentStore.SaveAsync(agent, ct);

            eventBus.Publish(new AgentStateChangedEvent(
                tenantId.ToString(),
                agent.AgentId.Value,
                agent.DisplayName,
                oldState.ToString(),
                target.ToString()));

            return await OkDtoAsync(conversationStore, capacityResolver, tenantId, agent, ct);
        }

        // Non-deferrable target (Available/Busy/Offline/ACW/…).
        if (agent.HasPendingPause)
        {
            // Cancel the pending pause AND apply the requested target in one save.
            agent.PendingState = null;
            agent.PendingReason = null;
            agent.PendingSince = null;
            agent.TransitionTo(target, clock.GetUtcNow());
            await agentStore.SaveAsync(agent, ct);

            // Emit the unpause (pending-cleared) event ONLY when the new target is
            // ROUTABLE. For a non-routable target (ACW/Offline) the AgentStateChangedEvent
            // below already keeps the agent paused, and emitting pending(null) first
            // would cause an unpause→re-pause flicker + a redundant AMI QueuePause —
            // the same contract the force path upholds.
            if (AgentStateMachine.IsRoutable(target))
            {
                eventBus.Publish(new AgentPendingStateChangedEvent(
                    tenantId.ToString(),
                    agent.AgentId.Value,
                    agent.DisplayName,
                    null));
            }
            eventBus.Publish(new AgentStateChangedEvent(
                tenantId.ToString(),
                agent.AgentId.Value,
                agent.DisplayName,
                oldState.ToString(),
                target.ToString()));

            return await OkDtoAsync(conversationStore, capacityResolver, tenantId, agent, ct);
        }

        // Not pending, any target — existing immediate path.
        agent.TransitionTo(target, clock.GetUtcNow());
        await agentStore.SaveAsync(agent, ct);

        eventBus.Publish(new AgentStateChangedEvent(
            tenantId.ToString(),
            agent.AgentId.Value,
            agent.DisplayName,
            oldState.ToString(),
            target.ToString()));

        return await OkDtoAsync(conversationStore, capacityResolver, tenantId, agent, ct);
    }

    // W4 (A5) — cancel a pending pause WITHOUT applying it. The agent stays in its
    // current (routable) state and resumes accepting new work. Publishes the unpause
    // (AgentPendingStateChangedEvent null). Idempotent: a no-op 200 when not pending.
    private static async Task<Results<Ok<AgentMeResponseDto>, NotFound>> CancelPendingPause(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IConversationStore conversationStore,
        [FromServices] IAgentCapacityResolver capacityResolver,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        var agent = await agentStore.GetByUserIdAsync(tenantId, userId, ct);
        if (agent is null)
            return TypedResults.NotFound();

        if (agent.HasPendingPause)
        {
            agent.PendingState = null;
            agent.PendingReason = null;
            agent.PendingSince = null;
            await agentStore.SaveAsync(agent, ct);

            eventBus.Publish(new AgentPendingStateChangedEvent(
                tenantId.ToString(),
                agent.AgentId.Value,
                agent.DisplayName,
                null));
        }

        return await OkDtoAsync(conversationStore, capacityResolver, tenantId, agent, ct);
    }

    // W4 (A5) — apply a pending pause NOW (skip the drain wait). Flips State to the
    // pending target via ApplyPendingState() and publishes ONLY AgentStateChangedEvent.
    // Deliberately does NOT publish AgentPendingStateChangedEvent(null): doing so would
    // unpause then immediately re-pause for the now-non-routable state (the A4 flicker
    // contract). Idempotent: a no-op 200 when not pending.
    private static async Task<Results<Ok<AgentMeResponseDto>, NotFound>> ForcePendingPause(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IConversationStore conversationStore,
        [FromServices] IAgentCapacityResolver capacityResolver,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        var agent = await agentStore.GetByUserIdAsync(tenantId, userId, ct);
        if (agent is null)
            return TypedResults.NotFound();

        if (agent.HasPendingPause)
        {
            var oldState = agent.State;
            var target = agent.PendingState!.Value;
            agent.ApplyPendingState();
            await agentStore.SaveAsync(agent, ct);

            eventBus.Publish(new AgentStateChangedEvent(
                tenantId.ToString(),
                agent.AgentId.Value,
                agent.DisplayName,
                oldState.ToString(),
                target.ToString()));
        }

        return await OkDtoAsync(conversationStore, capacityResolver, tenantId, agent, ct);
    }

    private static async Task<Ok<AgentMeResponseDto>> OkDtoAsync(
        IConversationStore conversationStore,
        IAgentCapacityResolver capacityResolver,
        TenantId tenantId,
        Agent agent,
        CancellationToken ct)
    {
        var activeWork = await conversationStore.CountActiveWorkAsync(tenantId, agent.AgentId, ct);
        // W6-A6 — resolve the agent's EFFECTIVE capacity (tenant default merged with the per-agent
        // override, MaxVoice pinned). The `??` is a defensive fallback for the impossible-null case
        // (the agent was just loaded so it exists); merge over the hard defaults if it ever fires.
        var effective = await capacityResolver.ResolveAsync(tenantId, agent.AgentId, ct)
            ?? AgentCapacityResolver.ResolveEffective(agent.CapacityOverride, new ChannelCapacity());
        return TypedResults.Ok(AgentMeResponseDto.FromAgent(agent, activeWork, effective));
    }

    // W3 (A3) — heartbeat / proof-of-life. The browser refreshes the agent's
    // presence key on a short cadence so the liveness reaper can force Offline
    // any routable agent whose key has expired (closed laptop, dropped tab).
    // Does NOT change agent state — it is a pure liveness touch.
    private static async Task<IResult> Heartbeat(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IAgentLivenessStore livenessStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        var agent = await agentStore.GetByUserIdAsync(tenantId, userId, ct);
        if (agent is null)
            return Results.NotFound();

        var config = await authConfigStore.GetAsync(tenantId.ToString(), ct);
        var ttlSeconds = config?.AgentLivenessTimeoutSeconds ?? 60;

        // ttl <= 0 disables liveness reaping for this tenant — do not write a
        // presence key (the reaper ignores tenants with the feature off).
        if (ttlSeconds <= 0)
            return Results.NoContent();

        await livenessStore.TouchAsync(
            tenantId, agent.AgentId, TimeSpan.FromSeconds(ttlSeconds), Environment.MachineName, ct);

        return Results.NoContent();
    }

    // W3 (A4) — graceful departure / beacon target. The browser posts here on
    // pagehide (sendBeacon) and on explicit sign-off to tear the agent down to
    // Offline immediately rather than waiting on the liveness reaper. Idempotent:
    // safe to call when already Offline (still removes the key, still 204, no
    // redundant event). Uses ForceOffline() to bypass transition validation —
    // this is the reserved teardown path.
    private static async Task<IResult> GoOffline(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IAgentLivenessStore livenessStore,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        var agent = await agentStore.GetByUserIdAsync(tenantId, userId, ct);
        if (agent is null)
            return Results.NotFound();

        var oldState = agent.State;
        agent.ForceOffline();
        await agentStore.SaveAsync(agent, ct);
        await livenessStore.RemoveAsync(tenantId, agent.AgentId, ct);

        // Publish ONLY on a real transition so repeated pagehide beacons don't
        // spam RealtimeStateBridge → AMI QueuePause for an already-Offline agent.
        if (oldState != AgentState.Offline)
            eventBus.Publish(new AgentStateChangedEvent(
                tenantId.ToString(),
                agent.AgentId.Value,
                agent.DisplayName,
                oldState.ToString(),
                AgentState.Offline.ToString()));

        return Results.NoContent();
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }

    private static EntityId GetCurrentUserId(HttpContext context)
    {
        // ClaimTypes.NameIdentifier is the long-form claim name; with
        // JwtBearerOptions.MapInboundClaims=false (Program.cs:118) the JWT's
        // `sub` claim is NOT auto-remapped, so we MUST check the short-form
        // `sub` as the primary source for the user ID. Same pattern as
        // PermissionAuthorizationHandler.cs + OidcEndpoints.cs and friends.
        // Without this, GetByUserIdAsync gets a fresh random EntityId and
        // returns null → every authenticated /agents/me* call returns 404.
        //
        // API-key auth sets NameIdentifier to the KEY id, and the linked USER
        // id in the `user_id` claim (ApiKeyAuthenticationHandler) — same as
        // UsersMeEndpoint. So prefer `sub` (JWT) then `user_id` (API-key linked
        // user) and only fall back to NameIdentifier. Without `user_id`, an
        // API key whose linked user owns an agent would wrongly 404.
        var nameId = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return nameId is not null ? EntityId.From(nameId) : EntityId.New();
    }
}

// W4 (A5) — Reason is OPTIONAL and back-compat: the existing client posts only
// { state }. When a deferrable target is recorded as a PendingState, Reason (if
// present) is stored alongside it for the agent/supervisor UI.
internal sealed record UpdateAgentStateRequest(AgentState State, string? Reason = null);
