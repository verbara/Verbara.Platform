namespace Asterisk.Platform.Channels.Core;

public sealed record SendResult(
    bool Success,
    string? ExternalMessageId,
    string? ErrorCode,
    string? ErrorMessage);
