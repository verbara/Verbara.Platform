using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Channels.Messenger;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Messenger webhook HMAC validation failed for tenant {TenantId}")]
    internal static partial void HmacValidationFailed(ILogger logger, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Messenger webhook payload")]
    internal static partial void DeserializeWebhookFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP error sending Messenger message to {Url}")]
    internal static partial void HttpError(ILogger logger, Exception exception, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messenger API error {StatusCode}: {Body}")]
    internal static partial void ApiError(ILogger logger, int statusCode, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Messenger send response")]
    internal static partial void DeserializeSendResponseFailed(ILogger logger, Exception exception);
}
