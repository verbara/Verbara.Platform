using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.AgentAssist.Engine;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Bridges <see cref="AgentAssistSupervisor"/> observable streams into
/// <see cref="PlatformEventBus"/> so agent assist events flow out via SSE.
/// </summary>
public sealed partial class AgentAssistBridge : IHostedService, IDisposable
{
    private readonly AgentAssistSupervisor _supervisor;
    private readonly PlatformEventBus _eventBus;
    private readonly ILogger<AgentAssistBridge> _logger;

    // Per-session composite subscription (4 observables per session)
    private readonly ConcurrentDictionary<string, IDisposable> _sessionSubs = new();

    private IDisposable? _startedSub;
    private IDisposable? _endedSub;

    public AgentAssistBridge(
        AgentAssistSupervisor supervisor,
        PlatformEventBus eventBus,
        ILogger<AgentAssistBridge> logger)
    {
        _supervisor = supervisor;
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startedSub = _supervisor.SessionStarted.Subscribe(OnSessionStarted);
        _endedSub   = _supervisor.SessionEnded.Subscribe(OnSessionEnded);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    private void OnSessionStarted(AgentAssistSession session)
    {
        var sessionId = session.SessionId;
        var tenantId  = session.CallSession?.TenantId?.ToString() ?? string.Empty;
        var agentId   = session.CallSession?.AgentId?.ToString()  ?? string.Empty;

        var suggestionSub = session.Suggestions.Subscribe(s =>
        {
            try
            {
                _eventBus.Publish(new AgentAssistSuggestionEvent(
                    tenantId,
                    sessionId,
                    agentId,
                    s.Id,
                    s.Text,
                    s.Priority.ToString(),
                    s.Source.ToString(),
                    s.TriggerPhrase));
            }
            catch (Exception ex)
            {
                Log.PublishSuggestionFailed(_logger, sessionId, ex);
            }
        });

        var sentimentSub = session.Sentiment.Subscribe(r =>
        {
            try
            {
                _eventBus.Publish(new AgentAssistSentimentEvent(
                    tenantId,
                    sessionId,
                    agentId,
                    r.Speaker.ToString(),
                    r.Score,
                    r.Label.ToString(),
                    r.TriggerWords));
            }
            catch (Exception ex)
            {
                Log.PublishSentimentFailed(_logger, sessionId, ex);
            }
        });

        var complianceSub = session.ComplianceAlerts.Subscribe(a =>
        {
            try
            {
                _eventBus.Publish(new AgentAssistComplianceAlertEvent(
                    tenantId,
                    sessionId,
                    agentId,
                    a.RuleId,
                    a.Phrase,
                    a.Severity.ToString()));
            }
            catch (Exception ex)
            {
                Log.PublishComplianceAlertFailed(_logger, sessionId, ex);
            }
        });

        var transcriptSub = session.Transcripts.Subscribe(t =>
        {
            try
            {
                _eventBus.Publish(new AgentAssistTranscriptEvent(
                    tenantId,
                    sessionId,
                    agentId,
                    t.Speaker.ToString(),
                    t.Text,
                    IsFinal: true));
            }
            catch (Exception ex)
            {
                Log.PublishTranscriptFailed(_logger, sessionId, ex);
            }
        });

        // Combine all 4 subscriptions into a single disposable so cleanup is atomic
        var composite = new CompositeDisposable(suggestionSub, sentimentSub, complianceSub, transcriptSub);
        _sessionSubs[sessionId] = composite;
    }

    private void OnSessionEnded(AgentAssistSession session)
    {
        if (_sessionSubs.TryRemove(session.SessionId, out var subs))
            subs.Dispose();
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
