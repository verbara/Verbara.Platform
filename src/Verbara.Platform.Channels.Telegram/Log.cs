using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Channels.Telegram;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram webhook secret validation failed for tenant {TenantId}")]
    internal static partial void SecretValidationFailed(ILogger logger, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Telegram webhook payload")]
    internal static partial void DeserializeWebhookFailed(ILogger logger, Exception exception);

    // The request URL embeds the bot token, so these lines name the API method, never the URL.
    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP error calling Telegram {Method} (status {StatusCode})")]
    internal static partial void HttpError(ILogger logger, Exception exception, string method, int? statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram {Method} not sent: circuit {Circuit} rejected the call (open, or half-open with its probe in flight)")]
    internal static partial void CircuitRejected(ILogger logger, string circuit, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram API error {ErrorCode}: {Description}")]
    internal static partial void ApiError(ILogger logger, int? errorCode, string? description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Telegram send response")]
    internal static partial void DeserializeSendResponseFailed(ILogger logger, Exception exception);
}
