using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.WebChat;

/// <summary>
/// The in-process registry of WebChat sessions, indexed by session id (the transport handle) and by
/// visitor address (<c>webchat-&lt;guid&gt;</c>, the contact address agent replies are sent to).
/// </summary>
/// <remarks>
/// In-process by design: a session is known only to the replica that created it, so agent-reply delivery
/// is scoped to that replica (spec <c>webchat-visitor-identity</c>; a backplane is deferred).
/// </remarks>
public sealed class WebChatSessionManager
{
    private readonly ConcurrentDictionary<string, WebChatSession> _sessions = new();
    private readonly ConcurrentDictionary<string, WebChatSession> _byVisitorAddress = new(StringComparer.Ordinal);
    private readonly WebChatOptions _options;

    public WebChatSessionManager(IOptions<WebChatOptions> options)
    {
        _options = options.Value;
    }

    public Task<string> ConnectAsync(TenantId tenantId, EntityId conversationId, ChannelAddress customerAddress, IClock clock)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var now = clock.UtcNow;
        var session = new WebChatSession
        {
            SessionId = sessionId,
            ConversationId = conversationId,
            TenantId = tenantId,
            CustomerAddress = customerAddress,
            IsConnected = true,
            LastActivityAt = now,
            CreatedAt = now,
        };

        _sessions[sessionId] = session;
        // Last writer wins: a visitor address names one live session, the newest one created for it.
        _byVisitorAddress[customerAddress.Address] = session;
        return Task.FromResult(sessionId);
    }

    public Task DisconnectAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.IsConnected = false;
        }

        return Task.CompletedTask;
    }

    public Task<bool> ReconnectAsync(string sessionId, IClock clock)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return Task.FromResult(false);
        }

        if (IsExpired(session, clock))
        {
            return Task.FromResult(false);
        }

        session.IsConnected = true;
        session.LastActivityAt = clock.UtcNow;
        return Task.FromResult(true);
    }

    public Task<WebChatSession?> GetSessionAsync(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    /// <summary>
    /// The session holding <paramref name="visitorAddress"/> on this replica, or <c>null</c> when none does
    /// (never created here, or expired and cleaned up).
    /// </summary>
    public Task<WebChatSession?> GetSessionByVisitorAddressAsync(string visitorAddress)
    {
        _byVisitorAddress.TryGetValue(visitorAddress, out var session);
        return Task.FromResult(session);
    }

    /// <summary>
    /// Atomically marks the session as routed-to-queue. Returns <c>true</c> exactly once (the
    /// first inbound message of the session) and <c>false</c> afterwards or when the session is
    /// unknown — so the first message triggers queue assignment and later messages are no-ops.
    /// </summary>
    public bool TryMarkRouted(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) && session.TryMarkRouted();

    public Task<int> CleanupExpiredAsync(IClock clock)
    {
        var removed = 0;
        foreach (var key in _sessions.Keys)
        {
            if (_sessions.TryGetValue(key, out var session) && IsExpired(session, clock))
            {
                if (_sessions.TryRemove(key, out _))
                {
                    // Drop the visitor index entry only while it still names this session.
                    _byVisitorAddress.TryRemove(new KeyValuePair<string, WebChatSession>(session.CustomerAddress.Address, session));
                    removed++;
                }
            }
        }

        return Task.FromResult(removed);
    }

    public Task TouchAsync(string sessionId, IClock clock)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastActivityAt = clock.UtcNow;
        }

        return Task.CompletedTask;
    }

    public bool IsExpired(WebChatSession session, IClock clock) =>
        clock.UtcNow - session.LastActivityAt > _options.SessionTimeout;
}
