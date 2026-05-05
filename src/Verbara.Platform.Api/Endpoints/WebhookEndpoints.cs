using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/webhooks");

        group.MapPost("/{tenantId}/{channel}", HandleWebhook);
        group.MapGet("/{tenantId}/whatsapp", HandleWhatsAppVerification);
        group.MapGet("/{tenantId}/messenger", HandleMessengerVerification);
        group.MapGet("/{tenantId}/instagram", HandleInstagramVerification);
    }

    private static async Task<IResult> HandleWebhook(
        string tenantId,
        string channel,
        HttpRequest request,
        IChannelRegistry channelRegistry,
        IInboundMessagePipeline pipeline,
        IInboundRouter router,
        IConversationSwitchboard switchboard,
        IConversationService conversationService,
        [FromServices] IConversationStore conversationStore,
        [FromServices] IContactStore contactStore,
        IVirtualAgent virtualAgent,
        [FromServices] IConversationLifecycleService lifecycleService,
        [FromServices] DeliveryStatusHandler deliveryStatusHandler,
        [FromServices] ITenantChannelConfigStore configStore,
        [FromServices] PlatformEventBus eventBus,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("WebhookEndpoints");

        if (!TryParseChannelType(channel, out var channelType))
            return Results.BadRequest(new ErrorResponse($"Unknown channel: {channel}"));

        var tid = new TenantId(tenantId);

        // Verify channel is configured and active for this tenant
        var channelConfig = await configStore.GetAsync(tid, channelType, ct);
        if (channelConfig is null || !channelConfig.IsActive)
            return Results.NotFound();

        // Read body
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        var body = new ReadOnlyMemory<byte>(ms.ToArray());

        // Extract headers
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            if (header.Value.Count > 0)
                headers[header.Key] = header.Value.ToString();
        }

        WebhookResult result;
        try
        {
            var handler = channelRegistry.GetHandler(channelType);
            result = await handler.HandleAsync(body, headers, tid, ct);
        }
        catch (InvalidOperationException ex)
        {
#pragma warning disable CA1848 // Use LoggerMessage delegates
            logger.LogWarning(ex, "No handler registered for channel {Channel}", channel);
#pragma warning restore CA1848
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        if (result.Type == WebhookResultType.NewMessage && result.Message is not null)
        {
            var pipelineResult = await pipeline.ProcessAsync(result.Message, tid, channelType, ct);

            // Publish SSE events for real-time UI updates
            if (pipelineResult.IsNewConversation)
            {
                eventBus.Publish(new ConversationStateChangedEvent(
                    tid.Value, pipelineResult.ConversationId.Value, "", "Queued"));
            }

            eventBus.Publish(new ConversationMessageEvent(
                tid.Value, pipelineResult.ConversationId.Value,
                pipelineResult.MessageId.Value, "Inbound", channelType.ToString()));

            // Fetch conversation and contact by IDs returned from pipeline
            var conversation = await conversationStore.GetByIdAsync(tid, pipelineResult.ConversationId, ct);
            var contact = await contactStore.GetByIdAsync(tid, pipelineResult.ContactId, ct);

            if (conversation is not null && contact is not null)
            {
                // Route to queue
                var routingCtx = new RoutingContext(conversation, contact, channelType, result.Message.Content, tid);
                var routeResult = await router.RouteAsync(routingCtx, ct);

                await switchboard.AssignToQueueAsync(conversation.ConversationId, tid, routeResult.QueueId, ct);

                // Reload conversation to check owner after assignment
                var updated = await conversationStore.GetByIdAsync(tid, conversation.ConversationId, ct);
                if (updated?.Owner?.Kind == ConversationOwnerKind.Bot && updated.Owner.OwnerId.HasValue)
                {
                    var botResponse = await virtualAgent.ProcessMessageAsync(
                        updated.ConversationId, tid, result.Message.Content, ct);

                    if (botResponse.Action == BotResponseAction.Reply && botResponse.Messages is not null)
                    {
                        foreach (var reply in botResponse.Messages)
                        {
                            await conversationService.SendMessageAsync(
                                updated.ConversationId,
                                tid,
                                reply,
                                updated.Owner.OwnerId.Value,
                                ConversationOwnerKind.Bot,
                                ct);
                        }
                    }
                    else if (botResponse.Action == BotResponseAction.TransferToQueue)
                    {
                        if (botResponse.TargetQueueId is null)
                        {
#pragma warning disable CA1848 // Use LoggerMessage delegates
                            logger.LogWarning(
                                "Bot requested TransferToQueue for conversation {ConversationId} without a target queue; skipping handoff.",
                                updated.ConversationId.Value);
#pragma warning restore CA1848
                        }
                        else
                        {
                            await switchboard.TransferToQueueAsync(
                                updated.ConversationId,
                                tid,
                                botResponse.TargetQueueId.Value,
                                ct);

                            eventBus.Publish(new ConversationStateChangedEvent(
                                tid.Value, updated.ConversationId.Value, "Bot", "Queued"));
                        }
                    }
                    else if (botResponse.Action == BotResponseAction.EndConversation)
                    {
                        await lifecycleService.CloseAsync(tid, updated.ConversationId, wrapUp: null, ct);
                    }
                }
            }
        }
        else if (result.Type == WebhookResultType.StatusUpdate && result.StatusUpdate is not null)
        {
            await deliveryStatusHandler.HandleAsync(tid, result.StatusUpdate, ct);
        }

        return Results.Ok();
    }

    private static IResult HandleWhatsAppVerification(
        string tenantId,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && !string.IsNullOrEmpty(challenge))
            return Results.Ok(challenge);

        return Results.BadRequest();
    }

    private static IResult HandleMessengerVerification(
        string tenantId,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && !string.IsNullOrEmpty(challenge))
            return Results.Ok(challenge);

        return Results.BadRequest();
    }

    private static IResult HandleInstagramVerification(
        string tenantId,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && !string.IsNullOrEmpty(challenge))
            return Results.Ok(challenge);

        return Results.BadRequest();
    }

    private static bool TryParseChannelType(string channel, out ChannelType channelType)
    {
        channelType = channel.ToLowerInvariant() switch
        {
            "whatsapp" => ChannelType.WhatsApp,
            "sms" => ChannelType.Sms,
            "webchat" => ChannelType.WebChat,
            "email" => ChannelType.Email,
            "messenger" => ChannelType.Messenger,
            "instagram" => ChannelType.Instagram,
            "telegram" => ChannelType.Telegram,
            "twitter" => ChannelType.Twitter,
            "video" => ChannelType.Video,
            "rcs" => ChannelType.Rcs,
            "voice" => ChannelType.Voice,
            _ => (ChannelType)(-1),
        };
        return (int)channelType >= 0;
    }
}
