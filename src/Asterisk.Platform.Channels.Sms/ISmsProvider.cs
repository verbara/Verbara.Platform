namespace Asterisk.Platform.Channels.Sms;

public interface ISmsProvider
{
    Task<SmsSendResult> SendAsync(string from, string recipient, string body, CancellationToken ct);
    Task<SmsDeliveryStatus> GetStatusAsync(string messageId, CancellationToken ct);
}

public sealed record SmsSendResult(bool Success, string? MessageId, string? ErrorCode, string? ErrorMessage);

public enum SmsDeliveryStatus { Queued, Sent, Delivered, Failed, Undelivered }
