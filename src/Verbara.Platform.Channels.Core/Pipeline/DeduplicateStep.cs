using Verbara.Platform.Conversations.Stores;

namespace Verbara.Platform.Channels.Core.Pipeline;

public sealed class DeduplicateStep : IPipelineStep
{
    private readonly IMessageStore _messageStore;

    public DeduplicateStep(IMessageStore messageStore)
    {
        _messageStore = messageStore;
    }

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var existing = await _messageStore.FindByExternalIdAsync(
            context.TenantId,
            context.InboundMessage.ExternalMessageId,
            ct);

        if (existing is not null)
        {
            context.IsDuplicate = true;
            context.PersistedMessage = existing;
        }

        return context;
    }
}
