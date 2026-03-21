using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Channels.WhatsApp;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp webhook HMAC validation failed for tenant {TenantId}")]
    internal static partial void HmacValidationFailed(ILogger logger, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize WhatsApp webhook payload")]
    internal static partial void DeserializeWebhookFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending WhatsApp template '{Template}' to {To} (outside 24h window: {Outside})")]
    internal static partial void SendingTemplate(ILogger logger, string template, string to, bool outside);

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP error sending WhatsApp message to {Url}")]
    internal static partial void HttpError(ILogger logger, Exception exception, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp API error {StatusCode}: {Body}")]
    internal static partial void ApiError(ILogger logger, int statusCode, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize WhatsApp send response")]
    internal static partial void DeserializeSendResponseFailed(ILogger logger, Exception exception);
}
