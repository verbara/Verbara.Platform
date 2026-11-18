using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Channels.Twitter;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Twitter CRC validation failed for tenant {TenantId}")]
    internal static partial void CrcValidationFailed(ILogger logger, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Twitter webhook payload")]
    internal static partial void DeserializeWebhookFailed(ILogger logger, Exception exception);

    // The request path carries the recipient's user id, so these lines leave the URL out.
    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP error sending Twitter DM (status {StatusCode})")]
    internal static partial void HttpError(ILogger logger, Exception exception, int? statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Twitter DM not sent: circuit {Circuit} rejected the call (open, or half-open with its probe in flight)")]
    internal static partial void CircuitRejected(ILogger logger, string circuit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Twitter API error {StatusCode}: {Body}")]
    internal static partial void ApiError(ILogger logger, int statusCode, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize Twitter send response")]
    internal static partial void DeserializeSendResponseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Twitter webhook event type {EventType} ignored")]
    internal static partial void EventIgnored(ILogger logger, string? eventType);
}
