using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
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
    /// <summary>outbound-calls-reach-the-trunk: the resolved trunk names no dialable host (409, with <c>trunkId</c>).</summary>
    private const string TrunkNotDialable = "trunk-not-dialable";

    public static void MapVoiceEndpoints(this IEndpointRouteBuilder app)
    {
        // readiness-without-asterisk D5: both groups answer 503 voice-unavailable with Retry-After while this pod's
        // primary AMI connection is not Connected (VoiceAvailabilityGateExtensions), with the route's own typed body.
        var group = app.MapGroup("/conversations").RequireAuthorization("Authenticated").RequireOperationalTenant()
            .RequireVoiceConnection(static () => TypedResults.Json(
                new VoiceTransferResponse(false, VoiceAvailabilityGateExtensions.VoiceUnavailable),
                ApiJsonContext.Default.VoiceTransferResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable));
        // outbound-calls-reach-the-trunk: 409 trunk-not-dialable is declared explicitly, so the 200 is too (an explicit
        // Produces may suppress the inferred success response — SuccessResponseCaptureTests).
        group.MapPost("/{id}/voice-transfer", BlindTransfer)
            .Produces<VoiceTransferResponse>(StatusCodes.Status200OK)
            .Produces<VoiceTransferResponse>(StatusCodes.Status409Conflict)
            .Produces<VoiceTransferResponse>(StatusCodes.Status503ServiceUnavailable);

        // Outbound click-to-dial (3B.2d) — not conversation-scoped (the Conversation is created by the
        // dial), so it lives under /voice rather than /conversations/{id}.
        var voice = app.MapGroup("/voice").RequireAuthorization("Authenticated").RequireOperationalTenant()
            .RequireVoiceConnection(static () => TypedResults.Json(
                new VoiceDialResponse(false, null, VoiceAvailabilityGateExtensions.VoiceUnavailable),
                ApiJsonContext.Default.VoiceDialResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable));
        voice.MapPost("/dial", Dial)
            .Produces<VoiceDialResponse>(StatusCodes.Status200OK)
            .Produces<VoiceDialResponse>(StatusCodes.Status409Conflict)
            .Produces<VoiceDialResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<Results<Ok<VoiceTransferResponse>, BadRequest<VoiceTransferResponse>, NotFound, JsonHttpResult<VoiceTransferResponse>>> BlindTransfer(
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
            return TypedResults.BadRequest(new VoiceTransferResponse(false, "invalid-kind"));
        if (string.IsNullOrWhiteSpace(body.Target))
            return TypedResults.BadRequest(new VoiceTransferResponse(false, "missing-target"));

        var conversationId = EntityId.From(id);

        // Only the agent who owns the live call may transfer it.
        var userId = GetCurrentUserId(context);
        var agent = await agents.GetByUserIdAsync(tenantId, userId, ct);
        var conversation = await conversations.GetByIdAsync(tenantId, conversationId, ct);
        if (conversation is null)
            return TypedResults.NotFound();
        if (agent is null
            || conversation.Owner?.Kind != ConversationOwnerKind.Agent
            || conversation.Owner.OwnerId != agent.AgentId)
        {
            return TypedResults.Json(
                new VoiceTransferResponse(false, "not-owner"),
                ApiJsonContext.Default.VoiceTransferResponse,
                statusCode: StatusCodes.Status403Forbidden);
        }

        var outcome = await callControl.BlindTransferAsync(
            tenantId, conversationId, new VoiceTransferTarget(kind, body.Target), ct);

        if (outcome.Accepted)
            return TypedResults.Ok(new VoiceTransferResponse(true, null));
        if (outcome.Error == TrunkNotDialable)
        {
            return TypedResults.Json(
                new VoiceTransferResponse(false, outcome.Error, outcome.TrunkId),
                ApiJsonContext.Default.VoiceTransferResponse,
                statusCode: StatusCodes.Status409Conflict);
        }

        return TypedResults.BadRequest(new VoiceTransferResponse(false, outcome.Error));
    }

    private static async Task<Results<Ok<VoiceDialResponse>, BadRequest<VoiceDialResponse>, JsonHttpResult<VoiceDialResponse>>> Dial(
        HttpContext context,
        [FromBody] VoiceDialRequest body,
        [FromServices] IAgentOutboundDialService dialService,
        [FromServices] IAgentStore agents,
        [FromServices] IContactStore contacts,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = GetCurrentUserId(context);

        // Only a provisioned agent may place an outbound call (the A-leg rings their own endpoint).
        var agent = await agents.GetByUserIdAsync(tenantId, userId, ct);
        if (agent is null)
        {
            return TypedResults.Json(
                new VoiceDialResponse(false, null, "not-an-agent"),
                ApiJsonContext.Default.VoiceDialResponse,
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Number from the request, or resolved from the contact's voice address when dialing a contact.
        var number = body.ToNumber?.Trim();
        EntityId? contactId = null;
        if (!string.IsNullOrWhiteSpace(body.ContactId))
        {
            contactId = EntityId.From(body.ContactId);
            if (string.IsNullOrWhiteSpace(number))
            {
                var contact = await contacts.GetByIdAsync(tenantId, contactId.Value, ct);
                number = contact?.Addresses.FirstOrDefault(a => a.Channel == ChannelType.Voice)?.Address;
            }
        }

        if (string.IsNullOrWhiteSpace(number))
            return TypedResults.BadRequest(new VoiceDialResponse(false, null, "missing-number"));

        var outcome = await dialService.DialAsync(tenantId, agent.AgentId, number, contactId, ct);
        if (outcome.Accepted)
            return TypedResults.Ok(new VoiceDialResponse(true, outcome.CorrelationId, null));
        if (outcome.Error == TrunkNotDialable)
        {
            return TypedResults.Json(
                new VoiceDialResponse(false, null, outcome.Error, outcome.TrunkId),
                ApiJsonContext.Default.VoiceDialResponse,
                statusCode: StatusCodes.Status409Conflict);
        }

        return TypedResults.BadRequest(new VoiceDialResponse(false, null, outcome.Error));
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

/// <summary>
/// <c>Error</c> is a stable machine code (e.g. "channel-unknown", "not-owner", "voice-unavailable" on 503) on failure.
/// <c>TrunkId</c> is always written: the resolved trunk's id on 409 <c>trunk-not-dialable</c>, null otherwise.
/// </summary>
internal sealed record VoiceTransferResponse(
    bool Accepted,
    string? Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? TrunkId = null);

/// <summary>Click-to-dial request (3B.2d): dial <c>ToNumber</c>, or resolve the number from <c>ContactId</c>.</summary>
internal sealed record VoiceDialRequest(string? ToNumber, string? ContactId);

/// <summary>
/// <c>CorrelationId</c> is the tracked outbound Conversation id on success; <c>Error</c> a stable code otherwise
/// (<c>voice-unavailable</c> on 503).
/// <c>TrunkId</c> is always written: the resolved trunk's id on 409 <c>trunk-not-dialable</c>, null otherwise.
/// </summary>
internal sealed record VoiceDialResponse(
    bool Accepted,
    string? CorrelationId,
    string? Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? TrunkId = null);
