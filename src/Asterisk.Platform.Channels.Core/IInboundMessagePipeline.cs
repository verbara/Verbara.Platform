using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Core;

public interface IInboundMessagePipeline
{
    Task<PipelineResult> ProcessAsync(
        InboundMessage message,
        TenantId tenantId,
        ChannelType channel,
        CancellationToken ct);
}
