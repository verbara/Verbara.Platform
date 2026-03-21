using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues.Services;

namespace Asterisk.Platform.Switchboard;

public sealed class ConversationSwitchboard : IConversationSwitchboard
{
    private readonly IConversationStore _store;
    private readonly IAgentCapacityService _capacity;
    private readonly IClock _clock;

    public ConversationSwitchboard(
        IConversationStore store,
        IAgentCapacityService capacity,
        IClock clock)
    {
        _store = store;
        _capacity = capacity;
        _clock = clock;
    }

    public async Task<OwnershipResult> AssignToQueueAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId queueId,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.Queued, "Conversation not found.");

        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Queued)
            && conversation.State != ConversationState.Queued)
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Queued.");

        if (conversation.State != ConversationState.Queued)
            conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);

        var owner = ConversationOwner.ForQueue(queueId);
        conversation.Owner = owner;
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        return new OwnershipResult(true, owner, conversation.State, null);
    }

    public async Task<OwnershipResult> OfferToAgentAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId agentId,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.Queued, "Conversation not found.");

        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Offered))
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Offered.");

        conversation.TransitionTo(ConversationState.Offered, _clock.UtcNow);
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        return new OwnershipResult(true, conversation.Owner, conversation.State, null);
    }

    public async Task<OwnershipResult> AcceptAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId agentId,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.Offered, "Conversation not found.");

        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Active))
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Active.");

        var hasCapacity = await _capacity.HasCapacityAsync(tenantId, agentId, conversation.Channel, ct).ConfigureAwait(false);
        if (!hasCapacity)
            return new OwnershipResult(false, conversation.Owner, conversation.State, "Agent has no capacity.");

        conversation.TransitionTo(ConversationState.Active, _clock.UtcNow);
        var owner = ConversationOwner.ForAgent(agentId);
        conversation.Owner = owner;
        conversation.UpdatedAt = _clock.UtcNow;

        await _capacity.ReserveAsync(tenantId, agentId, conversation.Channel, ct).ConfigureAwait(false);
        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        return new OwnershipResult(true, owner, conversation.State, null);
    }

    public async Task<OwnershipResult> RejectAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId agentId,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.Offered, "Conversation not found.");

        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Queued))
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Queued.");

        conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        return new OwnershipResult(true, conversation.Owner, conversation.State, null);
    }

    public async Task<OwnershipResult> TransferToQueueAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId targetQueueId,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.Queued, "Conversation not found.");

        // Release old agent capacity if currently owned by an agent
        if (conversation.Owner?.Kind == ConversationOwnerKind.Agent && conversation.Owner.OwnerId.HasValue)
            await _capacity.ReleaseAsync(tenantId, conversation.Owner.OwnerId.Value, conversation.Channel, ct).ConfigureAwait(false);

        // Active → Queued requires going through Escalated per the state machine
        if (conversation.State == ConversationState.Active)
        {
            conversation.TransitionTo(ConversationState.Escalated, _clock.UtcNow);
            conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);
        }
        else if (ConversationStateMachine.CanTransition(conversation.State, ConversationState.Queued))
        {
            conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);
        }
        else
        {
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Queued.");
        }

        var owner = ConversationOwner.ForQueue(targetQueueId);
        conversation.Owner = owner;
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        return new OwnershipResult(true, owner, conversation.State, null);
    }

    public async Task<OwnershipResult> TransferToAgentAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId targetAgentId,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.Active, "Conversation not found.");

        // Release old agent capacity if currently owned by an agent
        if (conversation.Owner?.Kind == ConversationOwnerKind.Agent && conversation.Owner.OwnerId.HasValue)
            await _capacity.ReleaseAsync(tenantId, conversation.Owner.OwnerId.Value, conversation.Channel, ct).ConfigureAwait(false);

        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Active)
            && conversation.State != ConversationState.Active)
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Active.");

        // If already Active, stay Active (just change owner). Otherwise transition.
        if (conversation.State != ConversationState.Active)
            conversation.TransitionTo(ConversationState.Active, _clock.UtcNow);

        var owner = ConversationOwner.ForAgent(targetAgentId);
        conversation.Owner = owner;
        conversation.UpdatedAt = _clock.UtcNow;

        await _capacity.ReserveAsync(tenantId, targetAgentId, conversation.Channel, ct).ConfigureAwait(false);
        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        return new OwnershipResult(true, owner, conversation.State, null);
    }

    public async Task<OwnershipResult> ReturnToBotAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId botId,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.Queued, "Conversation not found.");

        // Release agent capacity if currently owned by agent
        if (conversation.Owner?.Kind == ConversationOwnerKind.Agent && conversation.Owner.OwnerId.HasValue)
            await _capacity.ReleaseAsync(tenantId, conversation.Owner.OwnerId.Value, conversation.Channel, ct).ConfigureAwait(false);

        // Path: Active → Escalated → Queued
        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Escalated))
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Escalated.");

        conversation.TransitionTo(ConversationState.Escalated, _clock.UtcNow);
        conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);

        var owner = ConversationOwner.ForBot(botId);
        conversation.Owner = owner;
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        return new OwnershipResult(true, owner, conversation.State, null);
    }

    private static OwnershipResult Fail(ConversationState currentState, string reason) =>
        new(false, null, currentState, reason);
}
