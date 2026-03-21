using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Services;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Core.Pipeline;

public sealed class InboundMessagePipeline : IInboundMessagePipeline
{
    private readonly DeduplicateStep _deduplicateStep;
    private readonly ContactResolutionStep _contactResolutionStep;
    private readonly ConversationResolutionStep _conversationResolutionStep;
    private readonly MessagePersistenceStep _messagePersistenceStep;

    public InboundMessagePipeline(
        IMessageStore messageStore,
        IContactIdentityResolver contactIdentityResolver,
        IConversationStore conversationStore,
        IConversationLifecycleService conversationLifecycleService)
    {
        _deduplicateStep = new DeduplicateStep(messageStore);
        _contactResolutionStep = new ContactResolutionStep(contactIdentityResolver);
        _conversationResolutionStep = new ConversationResolutionStep(conversationStore, conversationLifecycleService);
        _messagePersistenceStep = new MessagePersistenceStep(messageStore);
    }

    public async Task<PipelineResult> ProcessAsync(
        InboundMessage message,
        TenantId tenantId,
        ChannelType channel,
        CancellationToken ct)
    {
        var context = new PipelineContext
        {
            InboundMessage = message,
            TenantId = tenantId,
            Channel = channel,
        };

        // Step 1: Deduplication
        context = await _deduplicateStep.ExecuteAsync(context, ct);

        if (context.IsDuplicate)
        {
            // Short-circuit: resolve contact to populate ContactId, skip conversation + persistence
            context = await _contactResolutionStep.ExecuteAsync(context, ct);

            var duplicate = context.PersistedMessage!;
            return new PipelineResult(
                ConversationId: duplicate.ConversationId,
                ContactId: context.Contact!.ContactId,
                MessageId: duplicate.MessageId,
                IsNewConversation: false);
        }

        // Step 2: Contact resolution
        context = await _contactResolutionStep.ExecuteAsync(context, ct);

        // Step 3: Conversation resolution
        context = await _conversationResolutionStep.ExecuteAsync(context, ct);

        // Step 4: Message persistence
        context = await _messagePersistenceStep.ExecuteAsync(context, ct);

        var persisted = context.PersistedMessage!;
        return new PipelineResult(
            ConversationId: context.Conversation!.ConversationId,
            ContactId: context.Contact!.ContactId,
            MessageId: persisted.MessageId,
            IsNewConversation: context.IsNewConversation);
    }
}
