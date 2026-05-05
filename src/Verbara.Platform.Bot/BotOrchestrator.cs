using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Bot;

/// <summary>
/// Default implementation of <see cref="IVirtualAgent"/>.
/// Resolves bot configuration, drives a <see cref="IFlowExecutionEngine"/> for each conversation,
/// tracks turn counts, and emits <see cref="BotAnalyticsEvent"/> instances.
/// </summary>
internal sealed partial class BotOrchestrator : IVirtualAgent
{
    private readonly IBotConfigStore _configStore;
    private readonly IFlowExecutionEngine _flowEngine;
    private readonly BotAnalyticsCollector _analytics;
    private readonly ILogger<BotOrchestrator> _logger;

    // Tracks turn counts per conversation (in-process; keyed by conversationId value).
    private readonly Dictionary<string, int> _turnCounts = new(StringComparer.Ordinal);

    public BotOrchestrator(
        IBotConfigStore configStore,
        IFlowExecutionEngine flowEngine,
        BotAnalyticsCollector analytics,
        ILogger<BotOrchestrator> logger)
    {
        _configStore = configStore;
        _flowEngine = flowEngine;
        _analytics = analytics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BotResponse> ProcessMessageAsync(
        EntityId conversationId,
        TenantId tenantId,
        MessageEnvelope message,
        CancellationToken ct)
    {
        // 1. Resolve bot configuration.
        var config = await _configStore.GetDefaultAsync(tenantId, ct).ConfigureAwait(false);
        if (config is null)
        {
            LogNoBotConfig(tenantId.Value);
            return new BotResponse(BotResponseAction.EndConversation, null, null, null, "No bot configuration found.");
        }

        if (!config.IsActive)
        {
            LogBotInactive(config.BotId.Value, tenantId.Value);
            return new BotResponse(BotResponseAction.EndConversation, null, null, null, "Bot is inactive.");
        }

        if (config.DefaultFlowId is not { } defaultFlowId)
        {
            LogNoBotConfig(tenantId.Value);
            return new BotResponse(BotResponseAction.EndConversation, null, null, null, "Bot has no default flow configured.");
        }

        // 2. Increment turn count and check max-turns guard.
        _turnCounts.TryGetValue(conversationId.Value, out var turns);
        turns++;
        _turnCounts[conversationId.Value] = turns;

        if (turns > config.MaxTurnsBeforeHandoff)
        {
            LogMaxTurnsReached(conversationId.Value, turns);
            _analytics.Emit(new BotAnalyticsEvent(
                config.BotId, tenantId, conversationId,
                BotAnalyticsEventType.MaxTurnsReached,
                DateTimeOffset.UtcNow,
                TurnCount: turns,
                HandoffReason: "Max turns exceeded"));

            return new BotResponse(
                BotResponseAction.TransferToQueue,
                null,
                config.FallbackQueueId,
                null,
                "Max turns exceeded");
        }

        // 3. Get or start a flow execution.
        FlowExecution? active = await _flowEngine.GetActiveExecutionAsync(tenantId, conversationId, ct).ConfigureAwait(false);

        if (active is null)
        {
            active = await _flowEngine.StartAsync(tenantId, defaultFlowId, conversationId, ct).ConfigureAwait(false);

            _analytics.Emit(new BotAnalyticsEvent(
                config.BotId, tenantId, conversationId,
                BotAnalyticsEventType.ConversationStarted,
                DateTimeOffset.UtcNow,
                TurnCount: turns));

            LogExecutionStarted(conversationId.Value, defaultFlowId.Value);
        }

        FlowExecution execution = active;

        // 4. Process the message through the flow engine.
        FlowStepResult result;
        try
        {
            result = await _flowEngine.ProcessMessageAsync(execution.ExecutionId, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFlowError(conversationId.Value, ex);
            _analytics.Emit(new BotAnalyticsEvent(
                config.BotId, tenantId, conversationId,
                BotAnalyticsEventType.Failed,
                DateTimeOffset.UtcNow,
                TurnCount: turns));

            return new BotResponse(BotResponseAction.EndConversation, null, null, null, "Flow execution failed.");
        }

        // 5. Convert FlowStepResult to BotResponse.
        return result.Status switch
        {
            FlowExecutionStatus.HandedOff => BuildHandoffResponse(config, tenantId, conversationId, result, turns),
            FlowExecutionStatus.Completed => BuildEndResponse(config, tenantId, conversationId, turns),
            FlowExecutionStatus.Failed    => BuildFailedResponse(config, tenantId, conversationId, turns),
            _                             => BuildReplyResponse(result),
        };
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private static BotResponse BuildReplyResponse(FlowStepResult result)
        => new(BotResponseAction.Reply, result.OutboundMessages, null, null, null);

    private BotResponse BuildHandoffResponse(
        BotConfiguration config,
        TenantId tenantId,
        EntityId conversationId,
        FlowStepResult result,
        int turns)
    {
        var queueId = result.TargetQueueId ?? config.FallbackQueueId;

        _analytics.Emit(new BotAnalyticsEvent(
            config.BotId, tenantId, conversationId,
            BotAnalyticsEventType.HandedOff,
            DateTimeOffset.UtcNow,
            TurnCount: turns,
            HandoffReason: "Flow handoff"));

        return new BotResponse(BotResponseAction.TransferToQueue, result.OutboundMessages, queueId, result.Priority, "Flow handoff");
    }

    private BotResponse BuildEndResponse(
        BotConfiguration config,
        TenantId tenantId,
        EntityId conversationId,
        int turns)
    {
        _analytics.Emit(new BotAnalyticsEvent(
            config.BotId, tenantId, conversationId,
            BotAnalyticsEventType.Resolved,
            DateTimeOffset.UtcNow,
            TurnCount: turns));

        return new BotResponse(BotResponseAction.EndConversation, null, null, null, null);
    }

    private BotResponse BuildFailedResponse(
        BotConfiguration config,
        TenantId tenantId,
        EntityId conversationId,
        int turns)
    {
        _analytics.Emit(new BotAnalyticsEvent(
            config.BotId, tenantId, conversationId,
            BotAnalyticsEventType.Failed,
            DateTimeOffset.UtcNow,
            TurnCount: turns));

        return new BotResponse(BotResponseAction.EndConversation, null, null, null, "Flow failed.");
    }

    // ── LoggerMessage delegates ───────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "No bot configuration found for tenant '{TenantId}'")]
    private partial void LogNoBotConfig(string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bot '{BotId}' is inactive for tenant '{TenantId}'")]
    private partial void LogBotInactive(string botId, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation '{ConversationId}' reached max turns ({Turns})")]
    private partial void LogMaxTurnsReached(string conversationId, int turns);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started flow execution for conversation '{ConversationId}' using flow '{FlowId}'")]
    private partial void LogExecutionStarted(string conversationId, string flowId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flow execution failed for conversation '{ConversationId}'")]
    private partial void LogFlowError(string conversationId, Exception ex);
}
