using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/agents").RequireAuthorization("Authenticated").RequireOperationalTenant();

        group.MapGet("/me", GetCurrentAgent);
        group.MapPut("/me/state", UpdateAgentState);
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
