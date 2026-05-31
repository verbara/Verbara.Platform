using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.WebChat;

public sealed class WebChatSessionManager
{
    private readonly ConcurrentDictionary<string, WebChatSession> _sessions = new();
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
