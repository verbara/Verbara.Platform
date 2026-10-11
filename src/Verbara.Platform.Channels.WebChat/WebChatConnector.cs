using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat;

/// <summary>
/// Delivers an agent reply to a WebChat visitor. The destination address is the visitor address
/// (<c>webchat-&lt;guid&gt;</c>) of the conversation's contact; it resolves to the session holding that
/// address and the reply goes out on that session's socket (spec <c>webchat-visitor-identity</c>, D16).
/// </summary>
/// <remarks>
/// Scope: the session manager and the socket transport are in-process, so a reply reaches the visitor
/// only from the replica holding the socket. On any other replica no session holds the address and the
/// send fails with <see cref="SessionNotConnected"/>, exactly as for a closed session. Cross-replica
/// routing (a backplane) is deferred.
/// </remarks>
public sealed class WebChatConnector : IChannelConnector
{
    /// <summary>The error code of a send whose visitor has no connected session on this replica.</summary>
    public const string SessionNotConnected = "SESSION_NOT_CONNECTED";

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
        var visitorAddress = message.To.Address;
        var session = await _sessionManager.GetSessionByVisitorAddressAsync(visitorAddress).ConfigureAwait(false);

        if (session is null || !session.IsConnected)
        {
            return new SendResult(
                Success: false,
                ExternalMessageId: null,
                ErrorCode: SessionNotConnected,
                ErrorMessage: $"No connected WebChat session holds visitor address '{visitorAddress}' on this replica.");
        }

        await _transport.SendToClientAsync(session.SessionId, message.Content, ct).ConfigureAwait(false);

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
