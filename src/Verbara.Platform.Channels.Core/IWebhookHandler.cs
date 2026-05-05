using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

public interface IWebhookHandler
{
    ChannelType Channel { get; }
    Task<WebhookResult> HandleAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        TenantId tenantId,
        CancellationToken ct);
}
