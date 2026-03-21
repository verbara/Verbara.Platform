using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Core;

public interface IWebhookHandler
{
    ChannelType Channel { get; }
    Task<WebhookResult> HandleAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        TenantId tenantId,
        CancellationToken ct);
}
