using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Automation;

public sealed partial class AutomationEngine : IAutomationEngine
{
    private const int MaxActivationsPerEvent = 10;

    private readonly IAutomationRuleStore _ruleStore;
    private readonly IConditionEvaluator _conditionEvaluator;
    private readonly IActionExecutor _actionExecutor;
    private readonly IAutomationExecutionLogStore _logStore;
    private readonly IClock _clock;
    private readonly ILogger<AutomationEngine> _logger;

    // (conversationId, ruleId) → execution count
    private readonly ConcurrentDictionary<(string ConversationId, string RuleId), int> _executionCounts = new();

    public AutomationEngine(
        IAutomationRuleStore ruleStore,
        IConditionEvaluator conditionEvaluator,
        IActionExecutor actionExecutor,
        IAutomationExecutionLogStore logStore,
        IClock clock,
        ILogger<AutomationEngine> logger)
    {
        _ruleStore = ruleStore;
        _conditionEvaluator = conditionEvaluator;
        _actionExecutor = actionExecutor;
        _logStore = logStore;
        _clock = clock;
        _logger = logger;
    }

    public async Task ProcessEventAsync(AutomationEvent automationEvent, CancellationToken ct)
    {
        var rules = await _ruleStore.GetActiveByTriggerAsync(
            automationEvent.TenantId,
            automationEvent.Trigger,
            ct).ConfigureAwait(false);

        var ordered = rules
            .Where(r => r.IsActive)
            .OrderBy(r => r.Priority)
            .ToList();

        LogProcessingEvent(_logger, automationEvent.Trigger, ordered.Count, automationEvent.ConversationId.Value);

        var activationsThisEvent = 0;

        foreach (var rule in ordered)
        {
            if (activationsThisEvent >= MaxActivationsPerEvent)
            {
                LogLoopProtectionTriggered(_logger, automationEvent.ConversationId.Value);
                break;
            }

            var countKey = (automationEvent.ConversationId.Value, rule.RuleId.Value);
            var currentCount = _executionCounts.GetValueOrDefault(countKey, 0);

            if (currentCount >= rule.MaxExecutionsPerConversation)
            {
                LogMaxExecutionsReached(_logger, rule.RuleId.Value, automationEvent.ConversationId.Value, rule.MaxExecutionsPerConversation);
                continue;
            }

            var conditionsMatched = await EvaluateConditionsAsync(rule, automationEvent, ct).ConfigureAwait(false);

            if (!conditionsMatched)
            {
                await WriteLogAsync(rule, automationEvent, conditionsMatched: false, [], null, ct).ConfigureAwait(false);
                continue;
            }

            // Execute actions
            var executedActions = new List<AutomationActionType>();
            string? error = null;

            try
            {
                foreach (var action in rule.Actions)
                {
                    var success = await _actionExecutor.ExecuteAsync(action, automationEvent, ct).ConfigureAwait(false);
                    if (success)
                        executedActions.Add(action.Type);
                }

                _executionCounts.AddOrUpdate(countKey, 1, (_, c) => c + 1);
                activationsThisEvent++;

                LogRuleActivated(_logger, rule.RuleId.Value, rule.Name, automationEvent.ConversationId.Value);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                LogRuleExecutionError(_logger, ex, rule.RuleId.Value, automationEvent.ConversationId.Value);
            }

            await WriteLogAsync(rule, automationEvent, conditionsMatched: true, executedActions, error, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> EvaluateConditionsAsync(AutomationRule rule, AutomationEvent automationEvent, CancellationToken ct)
    {
        foreach (var condition in rule.Conditions)
        {
            var result = await _conditionEvaluator.EvaluateAsync(condition, automationEvent, ct).ConfigureAwait(false);
            if (!result)
                return false;
        }

        return true;
    }

    private async Task WriteLogAsync(
        AutomationRule rule,
        AutomationEvent automationEvent,
        bool conditionsMatched,
        IReadOnlyList<AutomationActionType> actionsExecuted,
        string? error,
        CancellationToken ct)
    {
        var log = new AutomationExecutionLog
        {
            LogId = EntityId.New(),
            RuleId = rule.RuleId,
            TenantId = automationEvent.TenantId,
            ConversationId = automationEvent.ConversationId,
            Trigger = automationEvent.Trigger,
            ConditionsMatched = conditionsMatched,
            ActionsExecuted = actionsExecuted,
            Error = error,
            ExecutedAt = _clock.UtcNow,
        };

        await _logStore.SaveAsync(log, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing {Trigger} event for conversation {ConversationId} — {RuleCount} candidate rules")]
    private static partial void LogProcessingEvent(ILogger logger, AutomationTrigger trigger, int ruleCount, string conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Loop protection: max activations reached for conversation {ConversationId}")]
    private static partial void LogLoopProtectionTriggered(ILogger logger, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rule {RuleId} skipped: max executions ({MaxExecutions}) reached for conversation {ConversationId}")]
    private static partial void LogMaxExecutionsReached(ILogger logger, string ruleId, string conversationId, int maxExecutions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rule {RuleId} '{RuleName}' activated for conversation {ConversationId}")]
    private static partial void LogRuleActivated(ILogger logger, string ruleId, string ruleName, string conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing rule {RuleId} for conversation {ConversationId}")]
    private static partial void LogRuleExecutionError(ILogger logger, Exception ex, string ruleId, string conversationId);
}
