namespace Asterisk.Platform.Channels.Rcs;

/// <summary>
/// Abstraction over the RCS Business Messaging (RBM) provider, allowing different
/// transport implementations (Google RBM API, MVNO aggregators, etc.).
/// </summary>
public interface IRcsProvider
{
    Task<RcsSendResult> SendMessageAsync(string recipient, RcsMessage message, CancellationToken ct);
    Task<RcsDeliveryStatus> GetStatusAsync(string messageId, CancellationToken ct);
    Task<RcsCapability> CheckCapabilityAsync(string phoneNumber, CancellationToken ct);
}

public sealed record RcsSendResult(bool Success, string? MessageId, string? ErrorCode);

public enum RcsDeliveryStatus { Queued, Sent, Delivered, Read, Failed }

public sealed record RcsCapability(bool IsRcsEnabled, IReadOnlyList<string> SupportedFeatures);
