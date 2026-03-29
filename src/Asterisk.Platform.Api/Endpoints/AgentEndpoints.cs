using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agents").RequireAuthorization("Authenticated");

        group.MapGet("/me", GetCurrentAgent);
        group.MapPut("/me/state", UpdateAgentState);
    }

    private static async Task<IResult> GetCurrentAgent(
        HttpContext context,
        IAgentStore agentStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        var agent = await agentStore.GetByUserIdAsync(tenantId, userId, ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static async Task<IResult> UpdateAgentState(
        HttpContext context,
        IAgentStore agentStore,
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
        var nameId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return nameId is not null ? EntityId.From(nameId) : EntityId.New();
    }
}

internal sealed record UpdateAgentStateRequest(AgentState State);
