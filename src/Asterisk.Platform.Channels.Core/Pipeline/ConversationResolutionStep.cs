using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Services;

namespace Asterisk.Platform.Channels.Core.Pipeline;

public sealed class ConversationResolutionStep : IPipelineStep
{
    private readonly IConversationStore _conversationStore;
    private readonly IConversationLifecycleService _lifecycleService;

    public ConversationResolutionStep(
        IConversationStore conversationStore,
        IConversationLifecycleService lifecycleService)
    {
        _conversationStore = conversationStore;
        _lifecycleService = lifecycleService;
    }

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var contact = context.Contact
            ?? throw new InvalidOperationException("Contact must be resolved before conversation resolution.");

        var existing = await _conversationStore.FindActiveByContactAsync(
            context.TenantId,
            contact.ContactId,
            context.Channel,
            ct);

        if (existing is not null)
        {
            context.Conversation = existing;
            context.IsNewConversation = false;
        }
        else
        {
            context.Conversation = await _lifecycleService.CreateAsync(
                context.TenantId,
                contact.ContactId,
                context.Channel,
                ct);
            context.IsNewConversation = true;
        }

        return context;
    }
}
