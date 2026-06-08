using System.Net.Http.Json;
using System.Text;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Automation;

public sealed partial class DefaultActionExecutor : IActionExecutor
{
    private readonly IConversationSwitchboard _switchboard;
    private readonly IConversationService _conversationService;
    private readonly IConversationLifecycleService _lifecycleService;
    private readonly ITimerStore _timerStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClock _clock;
    private readonly ILogger<DefaultActionExecutor> _logger;

    public DefaultActionExecutor(
        IConversationSwitchboard switchboard,
        IConversationService conversationService,
        IConversationLifecycleService lifecycleService,
        ITimerStore timerStore,
        IHttpClientFactory httpClientFactory,
        IClock clock,
        ILogger<DefaultActionExecutor> logger)
    {
        _switchboard = switchboard;
        _conversationService = conversationService;
        _lifecycleService = lifecycleService;
        _timerStore = timerStore;
        _httpClientFactory = httpClientFactory;
        _clock = clock;
        _logger = logger;
    }

    public async Task<bool> ExecuteAsync(AutomationAction action, AutomationEvent automationEvent, CancellationToken ct)
    {
        switch (action.Type)
        {
            case AutomationActionType.AssignToQueue:
                return await ExecuteAssignToQueueAsync(action, automationEvent, ct).ConfigureAwait(false);

            case AutomationActionType.SendMessage:
                return await ExecuteSendMessageAsync(action, automationEvent, ct).ConfigureAwait(false);

            case AutomationActionType.SetPriority:
            case AutomationActionType.AddTag:
            case AutomationActionType.SetMetadata:
                return await ExecuteSetMetadataAsync(action, automationEvent).ConfigureAwait(false);

            case AutomationActionType.CloseConversation:
                return await ExecuteCloseConversationAsync(automationEvent, ct).ConfigureAwait(false);

            case AutomationActionType.ScheduleTimer:
                return await ExecuteScheduleTimerAsync(action, automationEvent, ct).ConfigureAwait(false);

            case AutomationActionType.Webhook:
                return await ExecuteWebhookAsync(action, automationEvent, ct).ConfigureAwait(false);

            case AutomationActionType.TransferToBot:
            case AutomationActionType.EscalateToSupervisor:
            case AutomationActionType.SendSurvey:
                LogUnsupportedAction(_logger, action.Type);
                return true;

            default:
                LogUnknownAction(_logger, action.Type);
                return false;
        }
    }

    private async Task<bool> ExecuteAssignToQueueAsync(AutomationAction action, AutomationEvent automationEvent, CancellationToken ct)
    {
        if (!action.Config.TryGetValue("queueId", out var queueIdStr))
        {
            LogMissingConfig(_logger, "queueId", action.Type);
            return false;
        }

        var queueId = EntityId.From(queueIdStr);
        await _switchboard.AssignToQueueAsync(automationEvent.ConversationId, automationEvent.TenantId, queueId, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteSendMessageAsync(AutomationAction action, AutomationEvent automationEvent, CancellationToken ct)
    {
        if (!action.Config.TryGetValue("text", out var text))
        {
            LogMissingConfig(_logger, "text", action.Type);
            return false;
        }

        var envelope = new MessageEnvelope([new TextBlock(text)]);
        var systemId = EntityId.From("system");

        await _conversationService.SendMessageAsync(
            automationEvent.ConversationId,
            automationEvent.TenantId,
            envelope,
            systemId,
            ConversationOwnerKind.System,
            ct).ConfigureAwait(false);

        return true;
    }

    private Task<bool> ExecuteSetMetadataAsync(AutomationAction action, AutomationEvent automationEvent)
    {
        // Metadata updates are a no-op here — implementations backed by a store would persist them.
        // The action type drives semantics (SetPriority/AddTag/SetMetadata all use Config key-value pairs).
        LogMetadataAction(_logger, action.Type, automationEvent.ConversationId.Value);
        return Task.FromResult(true);
    }

    private async Task<bool> ExecuteCloseConversationAsync(AutomationEvent automationEvent, CancellationToken ct)
    {
        await _lifecycleService.CloseAsync(automationEvent.TenantId, automationEvent.ConversationId, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteScheduleTimerAsync(AutomationAction action, AutomationEvent automationEvent, CancellationToken ct)
    {
        if (!action.Config.TryGetValue("callbackRuleId", out var callbackRuleIdStr))
        {
            LogMissingConfig(_logger, "callbackRuleId", action.Type);
            return false;
        }

        if (!action.Config.TryGetValue("delaySeconds", out var delayStr) ||
            !double.TryParse(delayStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var delaySec))
        {
            LogMissingConfig(_logger, "delaySeconds", action.Type);
            return false;
        }

        var timer = new ScheduledTimer
        {
            TimerId = EntityId.New(),
            TenantId = automationEvent.TenantId,
            ConversationId = automationEvent.ConversationId,
            CallbackRuleId = EntityId.From(callbackRuleIdStr),
            FireAt = _clock.UtcNow.AddSeconds(delaySec),
            IsFired = false,
            CreatedAt = _clock.UtcNow,
        };

        await _timerStore.SaveAsync(timer, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteWebhookAsync(AutomationAction action, AutomationEvent automationEvent, CancellationToken ct)
    {
        if (!action.Config.TryGetValue("url", out var url))
        {
            LogMissingConfig(_logger, "url", action.Type);
            return false;
        }

        action.Config.TryGetValue("body", out var body);

        var renderedBody = body ?? $"{{\"conversationId\":\"{automationEvent.ConversationId}\",\"trigger\":\"{automationEvent.Trigger}\"}}";

        var client = _httpClientFactory.CreateClient("AutomationWebhook");
        using var content = new StringContent(renderedBody, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(new Uri(url), content, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            LogWebhookFailure(_logger, url, (int)response.StatusCode);

        return response.IsSuccessStatusCode;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automation action {ActionType} is not fully implemented — skipping")]
    private static partial void LogUnsupportedAction(ILogger logger, AutomationActionType actionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown automation action type {ActionType} — skipping")]
    private static partial void LogUnknownAction(ILogger logger, AutomationActionType actionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Missing config key '{Key}' for action {ActionType}")]
    private static partial void LogMissingConfig(ILogger logger, string key, AutomationActionType actionType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Executing {ActionType} metadata update on conversation {ConversationId}")]
    private static partial void LogMetadataAction(ILogger logger, AutomationActionType actionType, string conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook POST to {Url} returned {StatusCode}")]
    private static partial void LogWebhookFailure(ILogger logger, string url, int statusCode);
}
