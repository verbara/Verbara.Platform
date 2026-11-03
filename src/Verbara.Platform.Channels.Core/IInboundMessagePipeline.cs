using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

public interface IInboundMessagePipeline
{
    /// <summary>
    /// Deduplicates, resolves the contact and the conversation (claiming it, design D7, D8), and stores the message.
    /// </summary>
    /// <exception cref="InboundReturnClosedException">
    /// Processing failed after this delivery had committed a <c>Resolved → Closed</c> return close. The caller
    /// announces <see cref="InboundReturnClosedException.ClosedOnReturnId"/> and rethrows the inner exception.
    /// </exception>
    Task<PipelineResult> ProcessAsync(
        InboundMessage message,
        TenantId tenantId,
        ChannelType channel,
        CancellationToken ct);
}
