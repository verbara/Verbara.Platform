using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Automation;

public sealed partial class DefaultConditionEvaluator : IConditionEvaluator
{
    private readonly IContactStore _contactStore;
    private readonly ILogger<DefaultConditionEvaluator> _logger;

    public DefaultConditionEvaluator(IContactStore contactStore, ILogger<DefaultConditionEvaluator> logger)
    {
        _contactStore = contactStore;
        _logger = logger;
    }

    public async Task<bool> EvaluateAsync(AutomationCondition condition, AutomationEvent automationEvent, CancellationToken ct)
    {
        var fieldValue = await ResolveFieldAsync(condition.Field, automationEvent, ct).ConfigureAwait(false);

        if (fieldValue is null && condition.Operator is not ConditionOperator.IsEmpty and not ConditionOperator.IsNotEmpty)
        {
            LogUnresolvableField(_logger, condition.Field);
            return false;
        }

        return condition.Operator switch
        {
            ConditionOperator.Equals => string.Equals(fieldValue, condition.Value, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => !string.Equals(fieldValue, condition.Value, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Contains => fieldValue is not null && condition.Value is not null &&
                                          fieldValue.Contains(condition.Value, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.StartsWith => fieldValue is not null && condition.Value is not null &&
                                            fieldValue.StartsWith(condition.Value, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.GreaterThan => TryParseDouble(fieldValue, out var fv) &&
                                             TryParseDouble(condition.Value, out var cv) && fv > cv,
            ConditionOperator.LessThan => TryParseDouble(fieldValue, out var fv2) &&
                                          TryParseDouble(condition.Value, out var cv2) && fv2 < cv2,
            ConditionOperator.IsEmpty => string.IsNullOrEmpty(fieldValue),
            ConditionOperator.IsNotEmpty => !string.IsNullOrEmpty(fieldValue),
            _ => false,
        };
    }

    private async Task<string?> ResolveFieldAsync(string field, AutomationEvent automationEvent, CancellationToken ct)
    {
        if (string.Equals(field, "channel", StringComparison.OrdinalIgnoreCase))
            return automationEvent.Channel?.ToString();

        if (string.Equals(field, "conversation.state", StringComparison.OrdinalIgnoreCase))
            return automationEvent.State?.ToString();

        if (string.Equals(field, "message.content", StringComparison.OrdinalIgnoreCase))
            return ExtractMessageContent(automationEvent.Message);

        if (field.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            var key = field["metadata.".Length..];
            return automationEvent.Metadata is not null && automationEvent.Metadata.TryGetValue(key, out var metaValue)
                ? metaValue
                : null;
        }

        if (string.Equals(field, "contact.segment", StringComparison.OrdinalIgnoreCase) &&
            automationEvent.ContactId.HasValue)
        {
            var contact = await _contactStore.GetByIdAsync(
                automationEvent.TenantId,
                automationEvent.ContactId.Value,
                ct).ConfigureAwait(false);
            return contact?.Segment;
        }

        LogUnknownField(_logger, field);
        return null;
    }

    private static string? ExtractMessageContent(Message? message)
    {
        if (message is null)
            return null;

        foreach (var block in message.Content.Blocks)
        {
            if (block is TextBlock text)
                return text.Text;
        }

        return null;
    }

    private static bool TryParseDouble(string? value, out double result)
    {
        if (value is null)
        {
            result = 0;
            return false;
        }
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Field '{Field}' could not be resolved from event context")]
    private static partial void LogUnresolvableField(ILogger logger, string field);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown automation condition field '{Field}'")]
    private static partial void LogUnknownField(ILogger logger, string field);
}
