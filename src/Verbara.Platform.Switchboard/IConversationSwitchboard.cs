using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Switchboard;

public interface IConversationSwitchboard
{
    Task<OwnershipResult> AssignToQueueAsync(EntityId conversationId, TenantId tenantId, EntityId queueId, CancellationToken ct);
    Task<OwnershipResult> OfferToAgentAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<OwnershipResult> AcceptAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<OwnershipResult> RejectAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<OwnershipResult> TransferToQueueAsync(EntityId conversationId, TenantId tenantId, EntityId targetQueueId, CancellationToken ct);

    /// <summary>
    /// W5 — failover re-queue: returns an orphaned conversation to its queue at the FRONT
    /// (the customer was already engaged), releasing the offline agent's capacity. Reuses
    /// the TransferToQueue path.
    /// </summary>
    Task<OwnershipResult> RequeueToFrontAsync(EntityId conversationId, TenantId tenantId, EntityId targetQueueId, CancellationToken ct);
    /// <summary>
    /// Makes <paramref name="targetAgentId"/> the owner. <paramref name="change"/> names the call site
    /// (takeover, transfer, reassign) and its actor: the save commits with its licensed-agent ledger row.
    /// </summary>
    Task<OwnershipResult> TransferToAgentAsync(
        EntityId conversationId, TenantId tenantId, EntityId targetAgentId, OwnershipChange change, CancellationToken ct);
    Task<OwnershipResult> ReturnToBotAsync(EntityId conversationId, TenantId tenantId, EntityId botId, CancellationToken ct);
    Task<OwnershipResult> HoldAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<OwnershipResult> UnholdAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);

    /// <summary>
    /// close-chat-without-typification-licence (design D2/D3) — the one way an agent-held digital
    /// conversation enters <c>WrapUp</c>, and the one point its owner's capacity
    /// unit is released. Accepted from <c>Active</c>, <c>OnHold</c>
    /// and <c>Consulting</c> (the last two step through <c>Active</c>, existing edges
    /// only); the owner must be an agent and the channel must not be voice (the voice bridge owns that edge).
    /// The conversation must still be owned by <paramref name="expectedOwnerAgentId"/> — the owner the caller
    /// authorized its actor against — so a transfer that committed after that check makes the call fail
    /// instead of wrapping up the new owner's chat. The store's state-and-owner conditional update
    /// (<c>TryCommitWrapUpAsync</c>, predicated on that same owner) decides the winner: only the winner
    /// releases the owner's unit (with <see cref="CancellationToken.None"/>; a failed release is logged,
    /// never surfaced), mirrors <c>State</c>/<c>UpdatedAt</c> and publishes
    /// <c>ConversationStateChangedEvent(old → WrapUp)</c>. Concurrent wrap-ups and closes therefore release
    /// once; an ownership move (transfer, requeue, return-to-bot, hold) that loaded the row before the commit
    /// is not covered — see <c>IConversationStore.TryCommitWrapUpAsync</c>.
    /// </summary>
    /// <param name="tenantId">The conversation's tenant.</param>
    /// <param name="conversationId">The conversation to wrap up.</param>
    /// <param name="expectedOwnerAgentId">The agent the caller's authorization was decided against.</param>
    /// <param name="ct">Cancels the read and the conditional update; never the release after a won commit.</param>
    /// <returns>
    /// <see cref="OwnershipResult.Success"/> <c>true</c> with <c>NewState = WrapUp</c> and the unchanged owner
    /// when this call committed the wrap-up. <c>false</c> when the conversation does not exist, is not
    /// agent-held digital work, is no longer owned by <paramref name="expectedOwnerAgentId"/>, or the
    /// conditional update lost a race (a concurrent wrap-up, close or owner change) — nothing was changed
    /// or released; <see cref="OwnershipResult.NewState"/> carries the state
    /// read before the attempt. Callers map <c>false</c> to 409 <c>conversation-not-closable</c>
    /// (or re-read first when a lost race may already have closed the conversation, design D4).
    /// </returns>
    Task<OwnershipResult> WrapUpAsync(
        TenantId tenantId, EntityId conversationId, EntityId expectedOwnerAgentId, CancellationToken ct);

    /// <summary>
    /// conversations-close-after-inactivity (design D3, D4, D5) — closes one idle digital conversation the inactivity
    /// pass read. The close is the store's conditional update (<c>IConversationStore.TryCloseInactiveAsync</c>) on the
    /// state, owner and clock in <paramref name="request"/>, the values the pass read, so a message, transfer or close
    /// that committed since makes it match nothing. It checks
    /// <see cref="ConversationStateMachine.CanCloseForInactivity"/> on the read state and refuses a voice conversation
    /// without writing; it never goes through <c>Conversation.TransitionTo</c> or the lifecycle service's close.
    /// Only the winner releases the read owner's capacity unit — when the read state was <c>Active</c>, <c>OnHold</c>
    /// or <c>Consulting</c> and the owner an agent; <c>WaitingForCustomer</c> and <c>Resolved</c> hold none — with
    /// <see cref="CancellationToken.None"/>, a failed release logged and the close kept (D5), and publishes
    /// <c>ConversationStateChangedEvent(read state → Closed)</c> once. The audit entry, the close counter and the close
    /// log are the caller's (D6, D13), written from the returned outcome.
    /// </summary>
    /// <param name="tenantId">The conversation's tenant.</param>
    /// <param name="request">What the pass read for the conversation, and its window's cut-offs.</param>
    /// <param name="channel">The conversation's channel as read (voice is never closed here).</param>
    /// <param name="ct">Cancels the conditional update; never the release after a won close.</param>
    /// <returns>The reason and clock of a won close; <see langword="null"/> when nothing was closed or released.</returns>
    Task<InactivityCloseOutcome?> CloseForInactivityAsync(
        TenantId tenantId, InactivityCloseRequest request, ChannelType channel, CancellationToken ct);
}
