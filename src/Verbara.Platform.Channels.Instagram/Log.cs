using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Channels.Instagram;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Instagram webhook HMAC validation failed for tenant {TenantId}")]
    internal static partial void HmacValidationFailed(ILogger logger, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Instagram webhook payload")]
    internal static partial void DeserializeWebhookFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP error sending Instagram message to {Url}")]
    internal static partial void HttpError(ILogger logger, Exception exception, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Instagram API error {StatusCode}: {Body}")]
    internal static partial void ApiError(ILogger logger, int statusCode, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Instagram send response")]
    internal static partial void DeserializeSendResponseFailed(ILogger logger, Exception exception);
}
