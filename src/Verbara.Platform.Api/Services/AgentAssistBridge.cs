using System.Collections.Concurrent;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Bridges <see cref="AgentAssistSupervisor"/> observable streams into
/// <see cref="PlatformEventBus"/> so agent assist events flow out via SSE.
/// </summary>
public sealed partial class AgentAssistBridge : IHostedService, IDisposable
{
    /// <summary>
    /// Keyed-service name for the per-event <see cref="ResiliencePolicy"/> that wraps each
    /// observable publish pass. In-memory bus means the policy rarely fires, but it protects
    /// against downstream observable throws from poisoning the subscription.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.agent-assist-bridge";

    private readonly AgentAssistSupervisor _supervisor;
    private readonly PlatformEventBus _eventBus;
    private readonly IConversationStore _conversationStore;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<AgentAssistBridge> _logger;

    // Per-session composite subscription (4 observables per session)
    private readonly ConcurrentDictionary<string, IDisposable> _sessionSubs = new();

    private IDisposable? _startedSub;
    private IDisposable? _endedSub;

    public AgentAssistBridge(
        AgentAssistSupervisor supervisor,
        PlatformEventBus eventBus,
        IConversationStore conversationStore,
        ILogger<AgentAssistBridge> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _supervisor = supervisor;
        _eventBus = eventBus;
        _conversationStore = conversationStore;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget into the guarded async handler — OnNext must be synchronous + never throw.
        _startedSub = _supervisor.SessionStarted.Subscribe(s => _ = OnSessionStartedAsync(s));
        _endedSub   = _supervisor.SessionEnded.Subscribe(OnSessionEnded);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    private async Task OnSessionStartedAsync(AgentAssistSession session)
    {
        try
        {
            var sessionId = session.SessionId;
            var callSession = session.CallSession;
            var tenantId = callSession?.TenantId?.ToString() ?? string.Empty;

            // Derive the platform agentId (the SAME value the client filters on) from the member
            // interface, with a tenant guard + CallSession.AgentId fallback (see DeriveAgentId).
            var agentId = DeriveAgentId(tenantId, callSession?.AgentInterface, callSession?.AgentId?.ToString());

            // ConversationId is resolved AFTER subscribing (below). Subscribing FIRST + storing the
            // composite BEFORE the await is load-bearing: (a) the Rx subjects do not replay, so an
            // await before Subscribe would drop early emissions (finding #11); (b) a concurrent
            // OnSessionEnded during the await always finds the stored composite to dispose, so the
            // subscription can never be orphaned (finding #12). The Subscribe closures read this
            // mutable local lazily at emission time, so reassigning it post-await is picked up.
            var conversationId = string.Empty;

            var suggestionSub = session.Suggestions.Subscribe(s =>
                _ = PublishWithPolicyAsync(
                    sessionId,
                    () => _eventBus.Publish(new AgentAssistSuggestionEvent(
                        tenantId,
                        sessionId,
                        agentId,
                        conversationId,
                        s.Id,
                        s.Text,
                        s.Priority.ToString(),
                        s.Source.ToString(),
                        s.TriggerPhrase)),
                    Log.PublishSuggestionFailed));

            var sentimentSub = session.Sentiment.Subscribe(r =>
                _ = PublishWithPolicyAsync(
                    sessionId,
                    () => _eventBus.Publish(new AgentAssistSentimentEvent(
                        tenantId,
                        sessionId,
                        agentId,
                        conversationId,
                        r.Speaker.ToString(),
                        r.Score,
                        r.Label.ToString(),
                        r.TriggerWords)),
                    Log.PublishSentimentFailed));

            var complianceSub = session.ComplianceAlerts.Subscribe(a =>
                _ = PublishWithPolicyAsync(
                    sessionId,
                    () => _eventBus.Publish(new AgentAssistComplianceAlertEvent(
                        tenantId,
                        sessionId,
                        agentId,
                        conversationId,
                        a.RuleId,
                        a.Phrase,
                        a.Severity.ToString())),
                    Log.PublishComplianceAlertFailed));

            var transcriptSub = session.Transcripts.Subscribe(t =>
                _ = PublishWithPolicyAsync(
                    sessionId,
                    () => _eventBus.Publish(new AgentAssistTranscriptEvent(
                        tenantId,
                        sessionId,
                        agentId,
                        conversationId,
                        t.Speaker.ToString(),
                        t.Text,
                        IsFinal: true)),
                    Log.PublishTranscriptFailed));

            // Combine all 4 subscriptions into a single disposable so cleanup is atomic. Stored BEFORE
            // the await below so a concurrent OnSessionEnded can always find + dispose it.
            var composite = new CompositeDisposable(suggestionSub, sentimentSub, complianceSub, transcriptSub);
            _sessionSubs[sessionId] = composite;

            // Resolve the tracked voice Conversation by the SAME voice_linked_id key
            // VoiceConversationBridge writes, so agent-assist suggestions/sentiment/transcript bind to
            // the screen-popped conversation. Best-effort: an unresolved id leaves ConversationId empty
            // (the client routes nothing). The Subscribe closures above read `conversationId` lazily,
            // so assigning it here applies to all subsequent emissions.
            if (callSession is not null && !string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(callSession.LinkedId))
            {
                var conversation = await _conversationStore
                    .FindByVoiceLinkedIdAsync(new TenantId(tenantId), callSession.LinkedId, CancellationToken.None)
                    .ConfigureAwait(false);
                conversationId = conversation?.ConversationId.Value ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.SessionStartFailed(_logger, session.SessionId, ex);
        }
    }

    private void OnSessionEnded(AgentAssistSession session)
    {
        if (_sessionSubs.TryRemove(session.SessionId, out var subs))
            subs.Dispose();
    }

    /// <summary>
    /// Derives the platform agentId for agent-assist events: the id parsed from the realtime member
    /// interface (<c>PJSIP/{tenant}-agent-{id}</c>) when the tenant is known, else the raw
    /// <paramref name="fallbackAgentId"/> (<c>CallSession.AgentId</c>, often empty for app_queue),
    /// else empty. Guarding the parse on a non-empty tenant avoids a blank-prefix match that would
    /// silently collapse the id and drop every agent-targeted event client-side (review finding #13).
    /// </summary>
    internal static string DeriveAgentId(string tenantId, string? agentInterface, string? fallbackAgentId)
    {
        var parsed = string.IsNullOrEmpty(tenantId)
            ? null
            : AgentInterfaceParser.ExtractAgentId(tenantId, agentInterface)?.Value;
        return parsed ?? fallbackAgentId ?? string.Empty;
    }

    internal async Task PublishWithPolicyAsync(
        string sessionId,
        Action publish,
        Action<ILogger, string, Exception> onError)
    {
        try
        {
            await _policy.ExecuteAsync(
                ResiliencePolicyKey,
                innerCt =>
                {
                    publish();
                    return ValueTask.FromResult(0);
                },
                CancellationToken.None);
        }
        catch (CircuitBreakerOpenException)
        {
            Log.CircuitOpen(_logger, sessionId);
        }
        catch (Exception ex)
        {
            onError(_logger, sessionId, ex);
        }
    }

    public void Dispose()
    {
        _startedSub?.Dispose();
        _endedSub?.Dispose();

        foreach (var subs in _sessionSubs.Values)
            subs.Dispose();

        _sessionSubs.Clear();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "[AgentAssistBridge] Error publishing suggestion for session {SessionId}")]
        public static partial void PublishSuggestionFailed(ILogger logger, string sessionId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[AgentAssistBridge] Error publishing sentiment for session {SessionId}")]
        public static partial void PublishSentimentFailed(ILogger logger, string sessionId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[AgentAssistBridge] Error publishing compliance alert for session {SessionId}")]
        public static partial void PublishComplianceAlertFailed(ILogger logger, string sessionId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[AgentAssistBridge] Error publishing transcript for session {SessionId}")]
        public static partial void PublishTranscriptFailed(ILogger logger, string sessionId, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "[AgentAssistBridge] Circuit open for worker.agent-assist-bridge — dropping event for session {SessionId}")]
        public static partial void CircuitOpen(ILogger logger, string sessionId);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "[AgentAssistBridge] Failed to start session {SessionId} (conversation/agent resolution)")]
        public static partial void SessionStartFailed(ILogger logger, string sessionId, Exception exception);
    }

    /// <summary>Composes multiple <see cref="IDisposable"/> instances into one.</summary>
    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _disposables;

        internal CompositeDisposable(params IDisposable[] disposables) =>
            _disposables = disposables;

        public void Dispose()
        {
            foreach (var d in _disposables)
            {
                try { d.Dispose(); }
                catch { /* best-effort */ }
            }
        }
    }
}
