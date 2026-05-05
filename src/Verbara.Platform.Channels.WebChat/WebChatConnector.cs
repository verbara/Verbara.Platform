using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat;

public sealed class WebChatConnector : IChannelConnector
{
    private readonly WebChatSessionManager _sessionManager;
    private readonly IWebChatTransport _transport;

    public ChannelType Channel => ChannelType.WebChat;

    public WebChatConnector(WebChatSessionManager sessionManager, IWebChatTransport transport)
    {
        _sessionManager = sessionManager;
        _transport = transport;
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var sessionId = message.To.Address;
        var session = await _sessionManager.GetSessionAsync(sessionId).ConfigureAwait(false);

        if (session is null || !session.IsConnected)
        {
            return new SendResult(
                Success: false,
                ExternalMessageId: null,
                ErrorCode: "SESSION_NOT_CONNECTED",
                ErrorMessage: $"Session '{sessionId}' is not connected.");
        }

        await _transport.SendToClientAsync(sessionId, message.Content, ct).ConfigureAwait(false);

        var externalMessageId = Guid.NewGuid().ToString("N");
        return new SendResult(
            Success: true,
            ExternalMessageId: externalMessageId,
            ErrorCode: null,
            ErrorMessage: null);
    }

    public async Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        // For WebChat, messages sent to connected sessions are always Delivered
        await Task.CompletedTask.ConfigureAwait(false);
        return MessageDeliveryStatus.Delivered;
    }
}
