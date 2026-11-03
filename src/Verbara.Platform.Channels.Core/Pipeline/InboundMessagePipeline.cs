using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core.Pipeline;

public sealed class InboundMessagePipeline : IInboundMessagePipeline
{
    private readonly DeduplicateStep _deduplicateStep;
    private readonly ContactResolutionStep _contactResolutionStep;
    private readonly ConversationResolutionStep _conversationResolutionStep;
    private readonly MessagePersistenceStep _messagePersistenceStep;
    private readonly IConversationStore _conversationStore;

    public InboundMessagePipeline(
        IMessageStore messageStore,
        IContactIdentityResolver contactIdentityResolver,
        IConversationStore conversationStore,
        IConversationLifecycleService conversationLifecycleService,
        IClock clock)
    {
        _deduplicateStep = new DeduplicateStep(messageStore);
        _contactResolutionStep = new ContactResolutionStep(contactIdentityResolver);
        _conversationResolutionStep = new ConversationResolutionStep(conversationStore, conversationLifecycleService, clock);
        _messagePersistenceStep = new MessagePersistenceStep(messageStore);
        _conversationStore = conversationStore;
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
            // A replay: short-circuit before the claim, so it carries no ClosedOnReturnId (D8).
            // Resolve contact to populate ContactId, skip conversation + persistence
            context = await _contactResolutionStep.ExecuteAsync(context, ct);

            var duplicate = context.PersistedMessage!;
            return new PipelineResult(
                ConversationId: duplicate.ConversationId,
                ContactId: context.Contact!.ContactId,
                MessageId: duplicate.MessageId,
                IsNewConversation: false,
                IsDuplicate: true);
        }

        // Step 2: Contact resolution
        context = await _contactResolutionStep.ExecuteAsync(context, ct);

        try
        {
            // Step 3: Conversation resolution, ending with the claim (design D7, D8)
            context = await _conversationResolutionStep.ExecuteAsync(context, ct);

            // Step 4: Message persistence
            context = await _messagePersistenceStep.ExecuteAsync(context, ct);

            return await CompleteAsync(context, tenantId, ct);
        }
        catch (Exception ex) when (context.ClosedOnReturnId is { } closedOnReturn)
        {
            // Audit round 4, F1: the Resolved -> Closed close is committed and cannot be undone, and a retry of this
            // delivery finds the row closed, so it would never be announced. Hand the id to the caller with the failure;
            // the caller announces the close and rethrows the original exception.
            throw new InboundReturnClosedException(tenantId, closedOnReturn, ex);
        }
    }

    private async Task<PipelineResult> CompleteAsync(PipelineContext context, TenantId tenantId, CancellationToken ct)
    {
        var persisted = context.PersistedMessage!;
        if (context.IsDuplicate)
        {
            // Lost the insert race to an identical delivery: report the stored message, as Step 1 would have.
            // A conversation this delivery opened for it never got a message; left behind, it would be routed as
            // queued and owner-less on the customer's next message. Remove it unless a message has landed there.
            if (context.IsNewConversation)
            {
                await _conversationStore.DeleteIfNoMessagesAsync(
                    tenantId, context.Conversation!.ConversationId, ct);
            }

            // Not a replay (D8): when this delivery won the return close, it is the only one that owns it, so the
            // duplicate result keeps ClosedOnReturnId for the caller to announce. Nothing else is repeated.
            return new PipelineResult(
                ConversationId: persisted.ConversationId,
                ContactId: context.Contact!.ContactId,
                MessageId: persisted.MessageId,
                IsNewConversation: false,
                IsDuplicate: true,
                ClosedOnReturnId: context.ClosedOnReturnId);
        }

        return new PipelineResult(
            ConversationId: context.Conversation!.ConversationId,
            ContactId: context.Contact!.ContactId,
            MessageId: persisted.MessageId,
            IsNewConversation: context.IsNewConversation,
            ClosedOnReturnId: context.ClosedOnReturnId);
    }
}
