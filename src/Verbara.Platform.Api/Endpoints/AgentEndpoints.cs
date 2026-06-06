using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/agents").RequireAuthorization("Authenticated").RequireOperationalTenant();

        group.MapGet("/me", GetCurrentAgent);
        group.MapPut("/me/state", UpdateAgentState);
        group.MapPost("/me/heartbeat", Heartbeat);
        group.MapPost("/me/offline", GoOffline);
    }

    private static async Task<IResult> GetCurrentAgent(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        var agent = await agentStore.GetByUserIdAsync(tenantId, userId, ct);
        return agent is null
            ? Results.NotFound()
            : Results.Ok(AgentMeResponseDto.FromAgent(agent));
    }

    private static async Task<IResult> UpdateAgentState(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        PlatformEventBus eventBus,
        [FromBody] UpdateAgentStateRequest body,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        var agent = await agentStore.GetByUserIdAsync(tenantId, userId, ct);
        if (agent is null)
            return Results.NotFound();

        var oldState = agent.State;
        agent.TransitionTo(body.State);
        await agentStore.SaveAsync(agent, ct);

        eventBus.Publish(new AgentStateChangedEvent(
            tenantId.ToString(),
            agent.AgentId.Value,
            agent.DisplayName,
            oldState.ToString(),
            body.State.ToString()));

        return Results.Ok(agent);
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

internal sealed record UpdateAgentStateRequest(AgentState State);
