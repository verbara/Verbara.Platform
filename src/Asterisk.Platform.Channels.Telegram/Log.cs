using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Channels.Telegram;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram webhook secret validation failed for tenant {TenantId}")]
    internal static partial void SecretValidationFailed(ILogger logger, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Telegram webhook payload")]
    internal static partial void DeserializeWebhookFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP error sending Telegram message to {Url}")]
    internal static partial void HttpError(ILogger logger, Exception exception, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram API error {ErrorCode}: {Description}")]
    internal static partial void ApiError(ILogger logger, int? errorCode, string? description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Telegram send response")]
    internal static partial void DeserializeSendResponseFailed(ILogger logger, Exception exception);
}
