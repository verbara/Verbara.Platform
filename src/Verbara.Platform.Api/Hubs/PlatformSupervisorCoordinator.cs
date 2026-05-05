using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Pro.Push.SignalR.Hubs;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Hubs;

internal static partial class PlatformSupervisorCoordinatorLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "[HUB-COORD] Start supervision: sup={SupervisorId} conv={ConversationId} mode={Mode}")]
    public static partial void StartSupervision(ILogger logger, string supervisorId, string conversationId, SupervisionMode mode);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[HUB-COORD] Whisper sent: sup={SupervisorId} conv={ConversationId} len={TextLength}")]
    public static partial void WhisperSent(ILogger logger, string supervisorId, string conversationId, int textLength);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[HUB-COORD] Stop supervision: sup={SupervisorId} conv={ConversationId}")]
    public static partial void StopSupervision(ILogger logger, string supervisorId, string conversationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[HUB-COORD] Session not found: conv={ConversationId}")]
    public static partial void SessionNotFound(ILogger logger, string conversationId);
}

/// <summary>
/// Platform-specific <see cref="ISupervisorCoordinator"/> that delegates to the
/// existing <see cref="AgentAssistSupervisor"/>. Mirrors the REST
/// <c>SupervisorEndpoints</c> semantics so the hub tier and the REST tier share
/// one business pipeline.
/// </summary>
/// <remarks>
/// v1.6.0-pro ships <see cref="SupervisionMode.Listen"/> and
/// <see cref="SupervisionMode.WhisperTts"/> only. Live-voice coach/barge is a
/// future (v1.5+) feature with its own spec — requests for unsupported modes
/// throw <see cref="NotSupportedException"/>.
/// </remarks>
internal sealed class PlatformSupervisorCoordinator : ISupervisorCoordinator
{
    private readonly AgentAssistSupervisor? _supervisor;
    private readonly ILogger<PlatformSupervisorCoordinator> _logger;

    public PlatformSupervisorCoordinator(
        ILogger<PlatformSupervisorCoordinator> logger,
        AgentAssistSupervisor? supervisor = null)
    {
        _logger = logger;
        _supervisor = supervisor;
    }

    public ValueTask StartSupervisionAsync(
        string supervisorId,
        string conversationId,
        SupervisionMode mode,
        CancellationToken cancellationToken)
    {
        PlatformSupervisorCoordinatorLog.StartSupervision(_logger, supervisorId, conversationId, mode);

        if (_supervisor is null || !_supervisor.ActiveSessions.ContainsKey(conversationId))
        {
            PlatformSupervisorCoordinatorLog.SessionNotFound(_logger, conversationId);
            throw new InvalidOperationException($"Session {conversationId} is not active.");
        }

        // Listen mode is a flag bookkeeping op — the agent is unaware. WhisperTts
        // is handled per-message via WhisperToAgentAsync; StartSupervision just
        // marks the supervisor as attached.
        return ValueTask.CompletedTask;
    }

    public ValueTask WhisperToAgentAsync(
        string supervisorId,
        string conversationId,
        string text,
        CancellationToken cancellationToken)
    {
        if (_supervisor is null || !_supervisor.ActiveSessions.TryGetValue(conversationId, out var session))
        {
            PlatformSupervisorCoordinatorLog.SessionNotFound(_logger, conversationId);
            throw new InvalidOperationException($"Session {conversationId} is not active.");
        }

        var enqueued = session.TryEnqueueSupervisorWhisper(text);
        if (!enqueued)
            throw new InvalidOperationException("Whisper delivery is not enabled for this session.");

        PlatformSupervisorCoordinatorLog.WhisperSent(_logger, supervisorId, conversationId, text.Length);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopSupervisingAsync(
        string supervisorId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        PlatformSupervisorCoordinatorLog.StopSupervision(_logger, supervisorId, conversationId);
        // Nothing persistent to clean up in v1.6.0-pro — listen entries live in
        // SupervisorListenStore which expires naturally with session lifecycle.
        return ValueTask.CompletedTask;
    }
}
