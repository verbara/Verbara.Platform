using System.Text.Json;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Channels.Video;

/// <summary>
/// Handles incoming WebRTC signaling events from the video platform.
/// Parses session started, session ended, and participant joined/left events.
/// </summary>
public sealed class VideoWebhookHandler : IWebhookHandler
{
    private const string EventSessionStarted = "session.started";
    private const string EventSessionEnded = "session.ended";
    private const string EventParticipantJoined = "participant.joined";
    private const string EventParticipantLeft = "participant.left";

    private readonly ILogger<VideoWebhookHandler> _logger;

    public ChannelType Channel => ChannelType.Video;

    public VideoWebhookHandler(ILogger<VideoWebhookHandler> logger)
    {
        _logger = logger;
    }

    public Task<WebhookResult> HandleAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        TenantId tenantId,
        CancellationToken ct)
    {
        return Task.FromResult(ParsePayload(body.Span));
    }

    private WebhookResult ParsePayload(ReadOnlySpan<byte> body)
    {
        VideoSignalingEvent? signalingEvent;
        try
        {
            signalingEvent = JsonSerializer.Deserialize(body, VideoJsonContext.Default.VideoSignalingEvent);
        }
        catch (JsonException ex)
        {
            Log.DeserializeWebhookFailed(_logger, ex);
            return Ignored();
        }

        if (signalingEvent?.Event is null || signalingEvent.SessionId is null)
            return Ignored();

        return signalingEvent.Event switch
        {
            EventSessionStarted => Ignored(), // Session lifecycle — no inbound message
            EventSessionEnded => Ignored(),   // Session lifecycle — no inbound message
            EventParticipantJoined => Ignored(), // Participant event — no inbound message
            EventParticipantLeft => Ignored(),   // Participant event — no inbound message
            _ => Ignored(),
        };
    }

    private static WebhookResult Ignored() =>
        new(WebhookResultType.Ignored, null, null);
}
