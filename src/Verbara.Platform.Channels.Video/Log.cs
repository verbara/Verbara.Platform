using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Channels.Video;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize video signaling webhook payload.")]
    internal static partial void DeserializeWebhookFailed(ILogger logger, Exception exception);
}
