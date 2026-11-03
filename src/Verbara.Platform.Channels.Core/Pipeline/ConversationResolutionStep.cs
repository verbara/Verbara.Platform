using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core.Pipeline;

/// <summary>
/// Finds the conversation an inbound message joins, and claims it before the message is stored
/// (conversations-close-after-inactivity design D7, D8):
/// <list type="bullet">
///   <item><description><b>Open, not <c>Resolved</c>:</b> <see cref="IConversationStore.TryClaimForInboundAsync"/>
///   advances its clock to the platform's time in one conditional update. If the row was closed, purged or moved to
///   <c>Resolved</c> since the read, the claim matches nothing and the step resolves again.</description></item>
///   <item><description><b><c>Resolved</c>:</b> <see cref="IConversationStore.TryCloseResolvedForReplyAsync"/> closes it
///   with <c>customer-returned</c>; win or lose, the step resolves again, so the message goes to a new conversation
///   (or to the replacement a concurrent delivery already opened). Only a win records
///   <see cref="PipelineContext.ClosedOnReturnId"/>, for the caller to announce.</description></item>
///   <item><description><b>None:</b> a new conversation, whose clock starts at its creation.</description></item>
/// </list>
/// So a message never lands in a conversation the inactivity close closed or the retention purge deleted meanwhile.
/// </summary>
public sealed class ConversationResolutionStep : IPipelineStep
{
    /// <summary>
    /// Re-resolutions before the step stops trying to join an existing row and opens a new one. Every lost claim means
    /// another writer changed the row it read, so this is reached only under a sustained storm of changes.
    /// </summary>
    internal const int MaxResolveAttempts = 8;

    private readonly IConversationStore _conversationStore;
    private readonly IConversationLifecycleService _lifecycleService;
    private readonly IClock _clock;

    public ConversationResolutionStep(
        IConversationStore conversationStore,
        IConversationLifecycleService lifecycleService,
        IClock clock)
    {
        _conversationStore = conversationStore;
        _lifecycleService = lifecycleService;
        _clock = clock;
    }

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var contact = context.Contact
            ?? throw new InvalidOperationException("Contact must be resolved before conversation resolution.");

        for (var attempt = 0; attempt < MaxResolveAttempts; attempt++)
        {
            var existing = await _conversationStore.FindActiveByContactAsync(
                context.TenantId,
                contact.ContactId,
                context.Channel,
                ct).ConfigureAwait(false);

            if (existing is null)
                break;

            // The platform's time, never the provider's timestamp: a late or retried delivery must not look old (D2, D7).
            var now = _clock.UtcNow;

            if (existing.State == ConversationState.Resolved)
            {
                // D8. One delivery closes at most one Resolved row, so the one flag it carries names the close it owns;
                // a second Resolved row (never expected) is left alone and the message opens a new conversation.
                if (context.ClosedOnReturnId is not null)
                    break;

                if (await _conversationStore.TryCloseResolvedForReplyAsync(
                        context.TenantId, existing.ConversationId, now, ct).ConfigureAwait(false))
                {
                    context.ClosedOnReturnId = existing.ConversationId;
                }

                continue;
            }

            if (await _conversationStore.TryClaimForInboundAsync(
                    context.TenantId, existing.ConversationId, now, ct).ConfigureAwait(false))
            {
                context.Conversation = existing;
                context.IsNewConversation = false;
                return context;
            }
        }

        context.Conversation = await _lifecycleService.CreateAsync(
            context.TenantId,
            contact.ContactId,
            context.Channel,
            ct).ConfigureAwait(false);
        context.IsNewConversation = true;

        return context;
    }
}
