using System.Net.WebSockets;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat;

/// <summary>
/// Delivers an agent reply to a WebChat visitor. The destination address is the visitor address
/// (<c>webchat-&lt;guid&gt;</c>) of the conversation's contact; it resolves to the session holding that
/// address and the reply goes out on that session's sockets (spec <c>webchat-visitor-identity</c>, D16). The
/// send succeeds only when a socket took the frame: a session with no socket attached yet, a socket that
/// closed mid-send, or a session of another tenant all fail with <see cref="SessionNotConnected"/>.
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
        ArgumentNullException.ThrowIfNull(message);
        var visitorAddress = message.To.Address;
        var session = await _sessionManager.GetSessionByVisitorAddressAsync(visitorAddress).ConfigureAwait(false);

        // A session of another tenant is never a destination, even when the address matches.
        if (session is null || !session.IsConnected || session.TenantId != message.TenantId)
            return NotConnected($"No connected WebChat session holds visitor address '{visitorAddress}' on this replica.");

        bool delivered;
        try
        {
            delivered = await _transport.SendToClientAsync(session.SessionId, message.Content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            // The socket closed between the check and the send, or a send was already outstanding on it
            // (ObjectDisposedException is an InvalidOperationException): a failed send, never a throw.
            delivered = false;
        }

        if (!delivered)
            return NotConnected($"No socket of the WebChat session holding visitor address '{visitorAddress}' took the reply.");

        var externalMessageId = Guid.NewGuid().ToString("N");
        return new SendResult(
            Success: true,
            ExternalMessageId: externalMessageId,
            ErrorCode: null,
            ErrorMessage: null);
    }

    private static SendResult NotConnected(string reason) =>
        new(Success: false, ExternalMessageId: null, ErrorCode: SessionNotConnected, ErrorMessage: reason);

    public async Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        // For WebChat, messages sent to connected sessions are always Delivered
        await Task.CompletedTask.ConfigureAwait(false);
        return MessageDeliveryStatus.Delivered;
    }
}
