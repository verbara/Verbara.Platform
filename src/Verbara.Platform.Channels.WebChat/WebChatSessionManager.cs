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
/// Expired sessions with no socket attached are evicted from both indexes by a sweep that session
/// creation runs at most once per <see cref="SweepInterval"/>, so the registry does not grow with every
/// anonymous session ever created.
/// </remarks>
public sealed class WebChatSessionManager
{
    /// <summary>The minimum time between two eviction sweeps triggered by session creation.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, WebChatSession> _sessions = new();
    private readonly ConcurrentDictionary<string, WebChatSession> _byVisitorAddress = new(StringComparer.Ordinal);
    private readonly WebChatOptions _options;
    private long _lastSweepTicks;

    public WebChatSessionManager(IOptions<WebChatOptions> options)
    {
        _options = options.Value;
    }

    public async Task<string> ConnectAsync(TenantId tenantId, EntityId conversationId, ChannelAddress customerAddress, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var sessionId = Guid.NewGuid().ToString("N");
        var now = clock.UtcNow;
        await SweepIfDueAsync(clock).ConfigureAwait(false);
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
        return sessionId;
    }

    /// <summary>
    /// Attaches a visitor socket to the session: it is connected again (after a reload or a network
    /// blip) and stays connected until its last socket detaches. <c>false</c> when the session is unknown
    /// or expired.
    /// </summary>
    public Task<bool> AttachSocketAsync(string sessionId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!_sessions.TryGetValue(sessionId, out var session) || IsExpired(session, clock))
            return Task.FromResult(false);

        session.AttachSocket(clock.UtcNow);
        return Task.FromResult(true);
    }

    /// <summary>Detaches one visitor socket; the session disconnects when no socket remains.</summary>
    public Task DetachSocketAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            session.DetachSocket();

        return Task.CompletedTask;
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
    /// (never created here, or expired with no socket attached and evicted by a sweep).
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

    /// <summary>
    /// Evicts every expired session with no socket attached. A session whose visitor is still attached is
    /// kept however idle it is: its socket is live and agent replies must still reach it.
    /// </summary>
    public Task<int> CleanupExpiredAsync(IClock clock)
    {
        var removed = 0;
        foreach (var (key, session) in _sessions.Where(e => e.Value.AttachedSockets == 0 && IsExpired(e.Value, clock)))
        {
            if (_sessions.TryRemove(new KeyValuePair<string, WebChatSession>(key, session)))
            {
                // Drop the visitor index entry only while it still names this session.
                _byVisitorAddress.TryRemove(new KeyValuePair<string, WebChatSession>(session.CustomerAddress.Address, session));
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    private Task<int> SweepIfDueAsync(IClock clock)
    {
        var nowTicks = clock.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (nowTicks - last < SweepInterval.Ticks
            || Interlocked.CompareExchange(ref _lastSweepTicks, nowTicks, last) != last)
        {
            return Task.FromResult(0);
        }

        return CleanupExpiredAsync(clock);
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
