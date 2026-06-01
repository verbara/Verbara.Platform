using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// Server-orchestrated voice call-control endpoints (3B.2c+). The in-browser softphone owns pure-media
/// control (hold/mute/DTMF) itself; anything touching another channel — blind transfer here, outbound
/// dial in 3B.2d — is an AMI side-effect routed through the leader-gated <see cref="IVoiceCallControlService"/>.
/// Registered only when <c>Asterisk:Ami:Hostname</c> is configured (the voice-AMI gate in Program.cs).
/// </summary>
internal static class VoiceEndpoints
{
    public static void MapVoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/conversations").RequireAuthorization("Authenticated").RequireOperationalTenant();
        group.MapPost("/{id}/voice-transfer", BlindTransfer);
    }

    private static async Task<IResult> BlindTransfer(
        string id,
        HttpContext context,
        [FromBody] VoiceTransferRequest body,
        [FromServices] IVoiceCallControlService callControl,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        if (!Enum.TryParse<VoiceTransferKind>(body.Kind, ignoreCase: true, out var kind))
            return Results.BadRequest(new VoiceTransferResponse(false, "invalid-kind"));
        if (string.IsNullOrWhiteSpace(body.Target))
            return Results.BadRequest(new VoiceTransferResponse(false, "missing-target"));

        var conversationId = EntityId.From(id);

        // Only the agent who owns the live call may transfer it.
        var userId = GetCurrentUserId(context);
        var agent = await agents.GetByUserIdAsync(tenantId, userId, ct);
        var conversation = await conversations.GetByIdAsync(tenantId, conversationId, ct);
        if (conversation is null)
            return Results.NotFound();
        if (agent is null
            || conversation.Owner?.Kind != ConversationOwnerKind.Agent
            || conversation.Owner.OwnerId != agent.AgentId)
        {
            return Results.Json(
                new VoiceTransferResponse(false, "not-owner"),
                ApiJsonContext.Default.VoiceTransferResponse,
                statusCode: StatusCodes.Status403Forbidden);
        }

        var outcome = await callControl.BlindTransferAsync(
            tenantId, conversationId, new VoiceTransferTarget(kind, body.Target), ct);

        return outcome.Accepted
            ? Results.Ok(new VoiceTransferResponse(true, null))
            : Results.BadRequest(new VoiceTransferResponse(false, outcome.Error));
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;
        throw new InvalidOperationException("Tenant ID not resolved");
    }

    private static EntityId GetCurrentUserId(HttpContext context)
    {
        // sub-first (MapInboundClaims=false), then API-key linked user_id, then NameIdentifier —
        // identical to AgentEndpoints.GetCurrentUserId.
        var nameId = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return nameId is not null ? EntityId.From(nameId) : EntityId.New();
    }
}

/// <summary>Blind-transfer request: <c>Kind</c> = "queue"|"agent", <c>Target</c> = the queue/agent id.</summary>
internal sealed record VoiceTransferRequest(string Kind, string Target);

/// <summary><c>Error</c> is a stable machine code (e.g. "channel-unknown", "not-owner") on failure.</summary>
internal sealed record VoiceTransferResponse(bool Accepted, string? Error);
