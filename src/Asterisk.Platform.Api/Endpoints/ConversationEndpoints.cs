using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Services;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Asterisk.Platform.Switchboard;
using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Dialer.Dispositions;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations").RequireAuthorization("Authenticated");

        group.MapGet("/", ListConversations);
        group.MapGet("/{id}", GetConversation);
        group.MapGet("/{id}/messages", GetMessages);
        group.MapPost("/{id}/messages", SendMessage);
        group.MapPost("/{id}/accept", AcceptConversation);
        group.MapPost("/{id}/reject", RejectConversation);
        group.MapPost("/{id}/transfer", TransferConversation);
        group.MapPost("/{id}/close", CloseConversation);
        group.MapPost("/{id}/wrapup", WrapUpConversation);
    }

    private static async Task<IResult> ListConversations(
        HttpContext context,
        IConversationStore store,
        ConversationState? state,
        string? queueId,
        string? agentId,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var query = new ConversationQuery
        {
            State = state,
            AssignedAgentId = agentId is not null ? EntityId.From(agentId) : null,
            Page = page,
            PageSize = pageSize,
        };

        var result = await store.ListAsync(tenantId, query, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetConversation(
        string id,
        HttpContext context,
        IConversationStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return conversation is null ? Results.NotFound() : Results.Ok(conversation);
    }

    private static async Task<IResult> GetMessages(
        string id,
        HttpContext context,
        IMessageStore store,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var messages = await store.GetConversationMessagesAsync(tenantId, EntityId.From(id), limit, offset, ct);
        return Results.Ok(messages);
    }

    private static async Task<IResult> SendMessage(
        string id,
        HttpContext context,
        [FromBody] SendMessageRequest body,
        IConversationService conversationService,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);

        var envelope = new MessageEnvelope(
        [
            new TextBlock(body.Text),
        ]);

        var message = await conversationService.SendMessageAsync(
            EntityId.From(id),
            tenantId,
            envelope,
            agentId,
            ConversationOwnerKind.Agent,
            ct);

        return Results.Ok(message);
    }

    private static async Task<IResult> AcceptConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);
        var result = await switchboard.AcceptAsync(EntityId.From(id), tenantId, agentId, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RejectConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);
        var result = await switchboard.RejectAsync(EntityId.From(id), tenantId, agentId, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> TransferConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        [FromBody] TransferRequest body,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        OwnershipResult result;

        if (body.TargetQueueId is not null)
            result = await switchboard.TransferToQueueAsync(EntityId.From(id), tenantId, EntityId.From(body.TargetQueueId), ct);
        else if (body.TargetAgentId is not null)
            result = await switchboard.TransferToAgentAsync(EntityId.From(id), tenantId, EntityId.From(body.TargetAgentId), ct);
        else
            return Results.BadRequest(new { error = "Either targetQueueId or targetAgentId must be specified" });

        return Results.Ok(result);
    }

    private static async Task<IResult> CloseConversation(
        string id,
        HttpContext context,
        IConversationStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return Results.NotFound();

        conversation.TransitionTo(ConversationState.Closed);
        await store.SaveAsync(conversation, ct);
        return Results.Ok(conversation);
    }

    private static async Task<IResult> WrapUpConversation(
        string id,
        HttpContext context,
        IConversationStore conversationStore,
        IWrapUpStore wrapUpStore,
        CampaignStoreBase campaignStore,
        DispositionCodeStoreBase dispositionCodeStore,
        PlatformEventBus eventBus,
        [FromBody] WrapUpRequest? body,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);
        var conversationId = EntityId.From(id);

        var conversation = await conversationStore.GetByIdAsync(tenantId, conversationId, ct);
        if (conversation is null)
            return Results.NotFound();

        try
        {
            conversation.TransitionTo(ConversationState.WrapUp);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        await conversationStore.SaveAsync(conversation, ct);

        var record = new WrapUpRecord
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            AgentId = agentId,
            DispositionId = body?.DispositionId is not null ? EntityId.From(body.DispositionId) : EntityId.New(),
            Notes = body?.Notes,
            Duration = TimeSpan.Zero,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await wrapUpStore.SaveAsync(record, ct);

        // ─── Campaign Disposition Bridge ─────────────────────────────────
        // If this is a campaign call, update the SDK call attempt + schedule callback
        if (body?.CampaignDispositionId is { } campaignDispoId)
        {
            var meta = conversation.Metadata;
            if (meta.TryGetValue("callAttemptId", out var attemptIdStr) &&
                long.TryParse(attemptIdStr, out var callAttemptId))
            {
                var tenantStr = tenantId.Value;

                // Update call attempt with disposition
                await campaignStore.UpdateCallAttemptDispositionAsync(
                    tenantStr, callAttemptId, campaignDispoId, body.Notes, ct);

                // Check if callback needs scheduling
                if (meta.TryGetValue("campaignId", out var campIdStr) &&
                    long.TryParse(campIdStr, out var campaignId) &&
                    meta.TryGetValue("contactId", out var contactIdStr) &&
                    long.TryParse(contactIdStr, out var contactId))
                {
                    // Fetch disposition to check TriggerCallback flag
                    var dispositions = await dispositionCodeStore.ListByCampaignAsync(tenantStr, campaignId, ct);
                    var dispo = dispositions.FirstOrDefault(d => d.Id == campaignDispoId);

                    if (dispo?.TriggerCallback == true && body.CallbackDate is { } cbDate &&
                        DateTimeOffset.TryParse(cbDate, out var scheduledAt))
                    {
                        var agentSubId = context.User.FindFirst("sub")?.Value ?? "";
                        await campaignStore.SaveCallbackAsync(
                            tenantStr, campaignId, contactId, scheduledAt, agentSubId, ct);
                    }

                    // Publish SSE event
                    eventBus.Publish(new CampaignDispositionSubmittedEvent(
                        tenantStr, campaignId, dispo?.Code ?? "",
                        context.User.FindFirst("sub")?.Value ?? ""));
                }
            }
        }

        return Results.Ok(record);
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }

    private static EntityId GetCurrentAgentId(HttpContext context)
    {
        var nameId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return nameId is not null ? EntityId.From(nameId) : EntityId.New();
    }
}

internal sealed record SendMessageRequest(string Text);
internal sealed record TransferRequest(string? TargetQueueId, string? TargetAgentId);
internal sealed record WrapUpRequest(
    string? DispositionId = null,
    string? Notes = null,
    long? CampaignDispositionId = null,
    string? CallbackDate = null,
    string? CallbackPhone = null);
