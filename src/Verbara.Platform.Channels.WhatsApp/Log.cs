using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Channels.WhatsApp;

internal static partial class Log
{
    [LoggerMessage(EventId = 8401, Level = LogLevel.Warning, Message = "WhatsApp webhook HMAC validation failed for tenant {TenantId}")]
    internal static partial void HmacValidationFailed(ILogger logger, string tenantId);

    [LoggerMessage(EventId = 8400, Level = LogLevel.Warning, Message = "WhatsApp webhook ignored for tenant {TenantId}: the tenant's WhatsApp configuration has no AppSecret (no process-wide fallback)")]
    internal static partial void AppSecretMissing(ILogger logger, string tenantId);

    [LoggerMessage(EventId = 8402, Level = LogLevel.Warning, Message = "WhatsApp webhook change ignored for tenant {TenantId}: metadata.phone_number_id is not the tenant's PhoneNumberId")]
    internal static partial void ForeignPhoneNumberId(ILogger logger, string tenantId);

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
