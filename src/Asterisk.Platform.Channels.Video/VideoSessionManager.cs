using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Video;

public sealed class VideoSessionManager
{
    private readonly ConcurrentDictionary<string, VideoSession> _sessionsBySessionId = new();
    private readonly ConcurrentDictionary<string, string> _sessionIdByConversation = new();
    private readonly IVideoTransport _transport;
    private readonly VideoOptions _options;
    private readonly ResiliencePolicy _policy;

    public VideoSessionManager(
        IVideoTransport transport,
        IOptions<VideoOptions> options,
        [FromKeyedServices(VideoConnector.ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _transport = transport;
        _options = options.Value;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public async Task<VideoSession> CreateAsync(VideoSessionRequest request, CancellationToken ct)
    {
        var session = await _policy.ExecuteAsync(
            VideoConnector.ResiliencePolicyKey,
            async innerCt => await _transport.CreateSessionAsync(request, innerCt).ConfigureAwait(false),
            ct).ConfigureAwait(false);
        _sessionsBySessionId[session.SessionId] = session;
        _sessionIdByConversation[request.ConversationId.Value] = session.SessionId;
        return session;
    }

    public async Task EndAsync(string sessionId, CancellationToken ct)
    {
        await _policy.ExecuteAsync(
            VideoConnector.ResiliencePolicyKey,
            async innerCt =>
            {
                await _transport.EndSessionAsync(sessionId, innerCt).ConfigureAwait(false);
                return 0;
            },
            ct).ConfigureAwait(false);

        if (_sessionsBySessionId.TryRemove(sessionId, out _))
        {
            // Remove from conversation index
            foreach (var kvp in _sessionIdByConversation)
            {
                if (kvp.Value == sessionId)
                {
                    _sessionIdByConversation.TryRemove(kvp.Key, out _);
                    break;
                }
            }
        }
    }

    public Task<VideoSession?> GetActiveSessionAsync(EntityId conversationId)
    {
        if (!_sessionIdByConversation.TryGetValue(conversationId.Value, out var sessionId))
            return Task.FromResult<VideoSession?>(null);

        _sessionsBySessionId.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task<int> CleanupExpiredAsync(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var key in _sessionsBySessionId.Keys)
        {
            if (!_sessionsBySessionId.TryGetValue(key, out var session))
                continue;

            if (now - session.CreatedAt > _options.SessionTimeout)
            {
                if (_sessionsBySessionId.TryRemove(key, out _))
                {
                    removed++;

                    foreach (var kvp in _sessionIdByConversation)
                    {
                        if (kvp.Value == key)
                        {
                            _sessionIdByConversation.TryRemove(kvp.Key, out _);
                            break;
                        }
                    }
                }
            }
        }

        return Task.FromResult(removed);
    }

    public bool IsExpired(VideoSession session, DateTimeOffset now) =>
        now - session.CreatedAt > _options.SessionTimeout;
}
