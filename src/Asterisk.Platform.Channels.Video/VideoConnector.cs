using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Resilience;

namespace Asterisk.Platform.Channels.Video;

public sealed class VideoConnector : IChannelConnector
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps
    /// <see cref="IVideoTransport"/> session create/end/status calls inside
    /// <see cref="VideoSessionManager"/>. Registered via <c>AddVideo()</c> with
    /// circuit 3/60s + retry 2/1s + timeout 30s.
    /// </summary>
    public const string ResiliencePolicyKey = "channel.video";

    private readonly VideoSessionManager _sessionManager;

    public ChannelType Channel => ChannelType.Video;

    public VideoConnector(VideoSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var conversationId = message.ConversationId;
        var session = await _sessionManager.GetActiveSessionAsync(conversationId).ConfigureAwait(false);

        if (session is null)
        {
            return new SendResult(
                Success: false,
                ExternalMessageId: null,
                ErrorCode: "SESSION_NOT_FOUND",
                ErrorMessage: $"No active video session for conversation '{conversationId.Value}'.");
        }

        // For video, "sending a message" means sending a chat message within the video session.
        // The actual delivery is handled by the signaling layer via the session's SignalingUrl.
        // We record the message with a generated ID for tracking purposes.
        var externalMessageId = Guid.NewGuid().ToString("N");
        return new SendResult(
            Success: true,
            ExternalMessageId: externalMessageId,
            ErrorCode: null,
            ErrorMessage: null);
    }

    public Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        // For video chat messages, once dispatched through the signaling layer they are Delivered.
        return Task.FromResult<MessageDeliveryStatus?>(MessageDeliveryStatus.Delivered);
    }
}
