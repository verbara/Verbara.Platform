using System.Globalization;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Switchboard;

public sealed class ConversationSwitchboard : IConversationSwitchboard
{
    private readonly IConversationStore _store;
    private readonly IAgentCapacityService _capacity;
    private readonly IAgentStore _agents;
    private readonly IClock _clock;
    private readonly PlatformEventBus _eventBus;
    private readonly IAgentAccountStatusLookup _accountStatus;

    public ConversationSwitchboard(
        IConversationStore store,
        IAgentCapacityService capacity,
        IAgentStore agents,
        IClock clock,
        PlatformEventBus eventBus,
        IAgentAccountStatusLookup accountStatus)
    {
        _store = store;
        _capacity = capacity;
        _agents = agents;
        _clock = clock;
        _eventBus = eventBus;
        _accountStatus = accountStatus;
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

        var oldState = conversation.State;
        if (conversation.State != ConversationState.Queued)
            conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);

        var owner = ConversationOwner.ForQueue(queueId);
        conversation.Owner = owner;
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, conversationId.Value, oldState.ToString(), conversation.State.ToString()));
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

        var now = _clock.UtcNow;
        conversation.TransitionTo(ConversationState.Offered, now);
        // Record whom the offer is for in the same write that makes it, so the offer can be accepted or
        // rejected only by that agent from the moment it exists (the offered event follows the save).
        conversation.SetMetadata(ConversationOffer.OfferedToKey, agentId.Value);
        conversation.SetMetadata(ConversationOffer.OfferedAtKey, now.ToString("O", CultureInfo.InvariantCulture));
        conversation.UpdatedAt = now;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        _eventBus.Publish(new ConversationOfferedEvent(
            tenantId.Value, conversationId.Value, agentId.Value,
            conversation.Owner?.OwnerId?.Value ?? "", conversation.Channel.ToString()));
        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, conversationId.Value, "Queued", "Offered"));
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

        if (ConversationOffer.IsOfferedToAnotherAgent(conversation, agentId))
            return Fail(conversation.State, OfferedToAnotherAgent);

        var hasCapacity = await _capacity.HasCapacityAsync(tenantId, agentId, conversation.Channel, ct).ConfigureAwait(false);
        if (!hasCapacity)
            return new OwnershipResult(false, conversation.Owner, conversation.State, "Agent has no capacity.");

        conversation.TransitionTo(ConversationState.Active, _clock.UtcNow);
        var owner = ConversationOwner.ForAgent(agentId);
        conversation.Owner = owner;
        conversation.UpdatedAt = _clock.UtcNow;

        await _capacity.ReserveAsync(tenantId, agentId, conversation.Channel, ct).ConfigureAwait(false);
        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        _eventBus.Publish(new ConversationAssignedEvent(
            tenantId.Value, conversationId.Value, agentId.Value, "", "", ""));
        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, conversationId.Value, "Offered", "Active"));
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

        if (ConversationOffer.IsOfferedToAnotherAgent(conversation, agentId))
            return Fail(conversation.State, OfferedToAnotherAgent);

        conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, conversationId.Value, "Offered", "Queued"));
        return new OwnershipResult(true, conversation.Owner, conversation.State, null);
    }

    public Task<OwnershipResult> TransferToQueueAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId targetQueueId,
        CancellationToken ct) =>
        // queuePriority 0 = normal transfer / back of the queue (FIFO). This also RESETS the
        // priority, so a previously-failovered conversation transferred normally returns to FIFO.
        TransferToQueueCoreAsync(conversationId, tenantId, targetQueueId, queuePriority: 0, ct);

    public Task<OwnershipResult> RequeueToFrontAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId targetQueueId,
        CancellationToken ct) =>
        // W5 — failover re-queue: priority -1 jumps the conversation to the front of its queue.
        TransferToQueueCoreAsync(conversationId, tenantId, targetQueueId, queuePriority: -1, ct);

    private async Task<OwnershipResult> TransferToQueueCoreAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId targetQueueId,
        int queuePriority,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.Queued, "Conversation not found.");

        // Re-queue path. OnHold/Consulting have no direct →Queued edge in the state machine,
        // but they ARE valid re-queue sources (W5 FailoverWorkStates), so first bring them back
        // to Active; then Active→Escalated→Queued. Active goes straight through; other transferable
        // states (e.g. Snoozed) use their direct →Queued edge. Decided before anything changes, so a
        // refused transfer leaves the owner's capacity reserved.
        var viaActive = conversation.State is ConversationState.OnHold or ConversationState.Consulting;
        var viaEscalated = viaActive || conversation.State == ConversationState.Active;
        if (!viaEscalated && !ConversationStateMachine.CanTransition(conversation.State, ConversationState.Queued))
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Queued.");

        await ReleaseOwnerCapacityAsync(conversation, tenantId, ct).ConfigureAwait(false);

        var oldState = conversation.State;
        if (viaActive)
            conversation.TransitionTo(ConversationState.Active, _clock.UtcNow);

        if (viaEscalated)
        {
            conversation.TransitionTo(ConversationState.Escalated, _clock.UtcNow);
            conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);
        }
        else
        {
            conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);
        }

        var owner = ConversationOwner.ForQueue(targetQueueId);
        conversation.Owner = owner;
        conversation.QueuePriority = queuePriority;
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, conversationId.Value, oldState.ToString(), conversation.State.ToString()));
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

        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Active)
            && conversation.State != ConversationState.Active)
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Active.");

        // The owner must be an agent of this tenant: an id that names no agent would leave the
        // conversation with an owner nobody is, and count its capacity against no one.
        // licensed-agent-metering (D2): nor may the owner be an agent whose user is not Active — a
        // suspended or deactivated user cannot act on it. Same failure as an unknown agent, checked
        // before anything is changed, so takeover, reassign and transfer all leave the conversation as
        // it was.
        var target = await _agents.GetByIdAsync(tenantId, targetAgentId, ct).ConfigureAwait(false);
        if (target is null
            || !await _accountStatus.IsActiveAsync(tenantId, target.UserId, ct).ConfigureAwait(false))
            return Fail(conversation.State, "Target agent not found.");

        await ReleaseOwnerCapacityAsync(conversation, tenantId, ct).ConfigureAwait(false);

        // If already Active, stay Active (just change owner). Otherwise transition.
        if (conversation.State != ConversationState.Active)
            conversation.TransitionTo(ConversationState.Active, _clock.UtcNow);

        var owner = ConversationOwner.ForAgent(targetAgentId);
        conversation.Owner = owner;
        conversation.UpdatedAt = _clock.UtcNow;

        await _capacity.ReserveAsync(tenantId, targetAgentId, conversation.Channel, ct).ConfigureAwait(false);
        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        _eventBus.Publish(new ConversationAssignedEvent(
            tenantId.Value, conversationId.Value, targetAgentId.Value, "", "", ""));
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

        // Path: Active → Escalated → Queued
        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Escalated))
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Escalated.");

        await ReleaseOwnerCapacityAsync(conversation, tenantId, ct).ConfigureAwait(false);

        conversation.TransitionTo(ConversationState.Escalated, _clock.UtcNow);
        conversation.TransitionTo(ConversationState.Queued, _clock.UtcNow);

        var owner = ConversationOwner.ForBot(botId);
        conversation.Owner = owner;
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, conversationId.Value, "Active", conversation.State.ToString()));
        return new OwnershipResult(true, owner, conversation.State, null);
    }

    public async Task<OwnershipResult> HoldAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId agentId,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.Active, "Conversation not found.");

        if (conversation.Owner?.Kind != ConversationOwnerKind.Agent ||
            conversation.Owner.OwnerId != agentId)
            return Fail(conversation.State, "Only the assigned agent can hold the conversation.");

        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.OnHold))
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to OnHold.");

        var oldState = conversation.State;
        conversation.TransitionTo(ConversationState.OnHold, _clock.UtcNow);
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, conversationId.Value, oldState.ToString(), "OnHold"));
        return new OwnershipResult(true, conversation.Owner, conversation.State, null);
    }

    public async Task<OwnershipResult> UnholdAsync(
        EntityId conversationId,
        TenantId tenantId,
        EntityId agentId,
        CancellationToken ct)
    {
        var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return Fail(ConversationState.OnHold, "Conversation not found.");

        if (conversation.Owner?.Kind != ConversationOwnerKind.Agent ||
            conversation.Owner.OwnerId != agentId)
            return Fail(conversation.State, "Only the assigned agent can unhold the conversation.");

        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Active))
            return Fail(conversation.State, $"Cannot transition from {conversation.State} to Active.");

        var oldState = conversation.State;
        conversation.TransitionTo(ConversationState.Active, _clock.UtcNow);
        conversation.UpdatedAt = _clock.UtcNow;

        await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, conversationId.Value, oldState.ToString(), "Active"));
        return new OwnershipResult(true, conversation.Owner, conversation.State, null);
    }

    private const string OfferedToAnotherAgent = "The conversation was offered to another agent.";

    // Releases the capacity the current agent owner holds for this conversation (none when an agent
    // does not own it). Called only once the move is known to go ahead.
    private Task ReleaseOwnerCapacityAsync(Conversation conversation, TenantId tenantId, CancellationToken ct) =>
        conversation.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerId }
            ? _capacity.ReleaseAsync(tenantId, ownerId, conversation.Channel, ct)
            : Task.CompletedTask;

    private static OwnershipResult Fail(ConversationState currentState, string reason) =>
        new(false, null, currentState, reason);
}
