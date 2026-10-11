using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/webhooks").RequireWebhookRateLimit();

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
        [FromServices] ITenantChannelConfigStore configStore,
        [FromServices] WebhookInboundProcessor processor,
        CancellationToken ct)
    {
        if (!TryParseChannelType(channel, out var channelType))
            return Results.BadRequest(new ErrorResponse($"Unknown channel: {channel}"));

        var tid = new TenantId(tenantId);

        // Verify channel is configured and active for this tenant
        var channelConfig = await configStore.GetAsync(tid, channelType, ct);
        if (channelConfig is null || !channelConfig.IsActive)
            return Results.NotFound();

        // Read body, at most 1 MB
        if (await BoundedRequestBody.ReadAsync(request, BoundedRequestBody.WebhookMaxBytes, ct) is not { } body)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var headers = request.Headers
            .Where(header => header.Value.Count > 0)
            .ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        // Only a channel whose module the host registered is served (whatsapp-works-for-real D1/D2); any
        // other configured channel stays unreachable, without exception text in the response.
        if (!channelRegistry.TryGetHandler(channelType, out var handler))
            return Results.NotFound();

        var result = await handler.HandleAsync(body, headers, tid, ct);

        // Every message and status of the delivery is stored before the 200; a replayed message has no side
        // effect, and one message's routing or bot failure never fails the others (WebhookInboundProcessor).
        await processor.ProcessAsync(tid, channelType, result, ct);
        return Results.Ok();
    }

    private static Task<IResult> HandleWhatsAppVerification(
        string tenantId,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        IChannelRegistry channelRegistry,
        CancellationToken ct) =>
        VerifySubscriptionAsync(ChannelType.WhatsApp, tenantId, mode, verifyToken, challenge, channelRegistry, ct);

    private static Task<IResult> HandleMessengerVerification(
        string tenantId,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        IChannelRegistry channelRegistry,
        CancellationToken ct) =>
        VerifySubscriptionAsync(ChannelType.Messenger, tenantId, mode, verifyToken, challenge, channelRegistry, ct);

    private static Task<IResult> HandleInstagramVerification(
        string tenantId,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        IChannelRegistry channelRegistry,
        CancellationToken ct) =>
        VerifySubscriptionAsync(ChannelType.Instagram, tenantId, mode, verifyToken, challenge, channelRegistry, ct);

    /// <summary>
    /// Meta's GET subscription handshake (whatsapp-works-for-real D5): the challenge is echoed as plain text
    /// only when the channel is registered, verifies subscriptions, and the token matches the tenant's own
    /// configured verify token. Anything else — wrong mode or token, no active configuration, or a channel
    /// the host does not serve — is 403 and never echoes the challenge.
    /// </summary>
    private static async Task<IResult> VerifySubscriptionAsync(
        ChannelType channel,
        string tenantId,
        string? mode,
        string? verifyToken,
        string? challenge,
        IChannelRegistry channelRegistry,
        CancellationToken ct)
    {
        if (!channelRegistry.TryGetHandler(channel, out var handler) || handler is not IWebhookSubscriptionVerifier verifier)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var echo = await verifier.VerifySubscriptionAsync(new TenantId(tenantId), mode, verifyToken, challenge, ct);
        return echo is null
            ? Results.StatusCode(StatusCodes.Status403Forbidden)
            : Results.Text(echo, "text/plain");
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
