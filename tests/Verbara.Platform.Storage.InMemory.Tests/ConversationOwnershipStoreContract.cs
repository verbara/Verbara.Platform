using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// contact-history-opens-by-assignment tasks 1.4, 1.5 and 2.1 (design D2, D4, D7), run against every store.
/// <list type="bullet">
/// <item><c>queue_id</c> is written by the store, never by a caller: a whole-row save whose owner is a queue stores
/// that queue, any other save keeps the stored value, so it names the last queue through routing, accept, transfer to
/// an agent, transfer to a queue, failover re-queue and close, and never clears.</item>
/// <item><see cref="ConversationQuery.AssignedAgentId"/> filters on the owner's kind as well as its id: a queue whose
/// id equals an agent's id does not own the agent's conversations.</item>
/// <item><see cref="IConversationStore.HoldsOpenConversationOfContactAsync"/> answers whether the agent owns a
/// non-terminal conversation of the contact in the tenant: an offer, a terminal row and a row moved to another owner
/// do not count.</item>
/// </list>
/// </summary>
/// <remarks>Linked into Verbara.Platform.Storage.Postgres.Tests.</remarks>
public abstract class ConversationOwnershipStoreContract
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly EntityId QueueOne = EntityId.From("queue-one");
    private static readonly EntityId QueueTwo = EntityId.From("queue-two");
    private static readonly EntityId QueueThree = EntityId.From("queue-three");
    private static readonly EntityId AgentA = EntityId.From("agent-a");
    private static readonly EntityId AgentB = EntityId.From("agent-b");

    protected static readonly TenantId Tenant = new("t-ownership");

    /// <summary>A new, empty store.</summary>
    protected abstract Task<IConversationStore> NewStoreAsync();

    private static Conversation NewConversation(ConversationOwner? owner, ConversationState state = ConversationState.Queued) =>
        new()
        {
            ConversationId = EntityId.From($"c-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.WhatsApp,
            State = state,
            Owner = owner,
            CreatedAt = Now.AddHours(-1),
        };

    private static async Task<Conversation> LoadAsync(IConversationStore store, Conversation conversation) =>
        (await store.GetByIdAsync(Tenant, conversation.ConversationId, CancellationToken.None))!;

    private static async Task<Conversation> SaveAndLoadAsync(IConversationStore store, Conversation conversation)
    {
        await store.SaveAsync(conversation, CancellationToken.None);
        return await LoadAsync(store, conversation);
    }

    // ── queue_id (design D7) ───────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ShouldNameTheLastQueue_WhenOwnershipMovesThroughRoutingAcceptTransferRequeueAndClose()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversation(owner: null);
        (await SaveAndLoadAsync(store, conversation)).QueueId.Should().BeNull("no queue has owned it yet");

        // Routed to a queue.
        var routed = await LoadAsync(store, conversation);
        routed.Owner = ConversationOwner.ForQueue(QueueOne);
        (await SaveAndLoadAsync(store, routed)).QueueId.Should().Be(QueueOne, "routing stores the queue");

        // Accepted by agent A.
        var accepted = await LoadAsync(store, conversation);
        accepted.Owner = ConversationOwner.ForAgent(AgentA);
        accepted.State = ConversationState.Active;
        (await SaveAndLoadAsync(store, accepted)).QueueId.Should().Be(QueueOne, "an assignment to an agent keeps the queue");

        // Transferred to agent B.
        var toAgent = await LoadAsync(store, conversation);
        toAgent.Owner = ConversationOwner.ForAgent(AgentB);
        (await SaveAndLoadAsync(store, toAgent)).QueueId.Should().Be(QueueOne, "a transfer to an agent keeps the queue");

        // Transferred to another queue.
        var toQueue = await LoadAsync(store, conversation);
        toQueue.Owner = ConversationOwner.ForQueue(QueueTwo);
        toQueue.State = ConversationState.Queued;
        (await SaveAndLoadAsync(store, toQueue)).QueueId.Should().Be(QueueTwo, "a transfer to a queue names the new queue");

        // Re-accepted, then re-queued to the front by a failover.
        var reaccepted = await LoadAsync(store, conversation);
        reaccepted.Owner = ConversationOwner.ForAgent(AgentA);
        reaccepted.State = ConversationState.Active;
        await store.SaveAsync(reaccepted, CancellationToken.None);
        var requeued = await LoadAsync(store, conversation);
        requeued.Owner = ConversationOwner.ForQueue(QueueThree);
        requeued.State = ConversationState.Queued;
        requeued.QueuePriority = -1;
        (await SaveAndLoadAsync(store, requeued)).QueueId.Should().Be(QueueThree, "a failover re-queue names its queue");

        // Accepted again and closed, by the whole-row save and by the compare-and-set close.
        var final = await LoadAsync(store, conversation);
        final.Owner = ConversationOwner.ForAgent(AgentB);
        final.State = ConversationState.WrapUp;
        await store.SaveAsync(final, CancellationToken.None);
        (await store.TryConditionalCloseWrapUpAsync(Tenant, conversation.ConversationId, Now, CancellationToken.None))
            .Should().BeTrue();
        var closed = await LoadAsync(store, conversation);
        closed.State.Should().Be(ConversationState.Closed);
        closed.QueueId.Should().Be(QueueThree, "a close never clears the queue");

        closed.SetMetadata("_closedReason", "inactivity");
        (await SaveAndLoadAsync(store, closed)).QueueId.Should().Be(QueueThree, "a whole-row save of a closed row keeps it");
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreTheQueue_WhenANewConversationIsBornOwnedByAQueue()
    {
        var store = await NewStoreAsync();

        var loaded = await SaveAndLoadAsync(store, NewConversation(ConversationOwner.ForQueue(QueueOne)));

        loaded.QueueId.Should().Be(QueueOne);
    }

    [Fact]
    public async Task SaveAsync_ShouldIgnoreTheCallersQueueId_WhenTheOwnerIsNotAQueue()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversation(ConversationOwner.ForAgent(AgentA), ConversationState.Active);
        conversation.QueueId = QueueTwo;

        var loaded = await SaveAndLoadAsync(store, conversation);

        loaded.QueueId.Should().BeNull("only a queue owner writes the queue; the store owns the column");
    }

    [Fact]
    public async Task SaveAsync_ShouldNotRollTheQueueBack_WhenAStaleCopyOwnedByAnAgentIsSaved()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversation(ConversationOwner.ForQueue(QueueOne));
        await store.SaveAsync(conversation, CancellationToken.None);
        var accepted = await LoadAsync(store, conversation);
        accepted.Owner = ConversationOwner.ForAgent(AgentA);
        accepted.State = ConversationState.Active;
        await store.SaveAsync(accepted, CancellationToken.None);
        var stale = Detach(await LoadAsync(store, conversation));

        var requeued = await LoadAsync(store, conversation);
        requeued.Owner = ConversationOwner.ForQueue(QueueTwo);
        requeued.State = ConversationState.Queued;
        await store.SaveAsync(requeued, CancellationToken.None);
        stale.UpdatedAt = Now;
        await store.SaveAsync(stale, CancellationToken.None);

        (await LoadAsync(store, conversation)).QueueId.Should().Be(QueueTwo,
            "a save whose owner is not a queue keeps the stored queue, whatever its copy carried");
    }

    // ── AssignedAgentId (design D4) ────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ShouldReturnOnlyTheAgentsConversations_WhenAQueueSharesTheAgentsId()
    {
        var store = await NewStoreAsync();
        var sharedId = EntityId.From($"shared-{Guid.NewGuid():N}");
        var ownedByQueue = NewConversation(ConversationOwner.ForQueue(sharedId));
        var ownedByAgent = NewConversation(ConversationOwner.ForAgent(sharedId), ConversationState.Active);
        var ownedByBot = NewConversation(ConversationOwner.ForBot(sharedId), ConversationState.Active);
        await store.SaveAsync(ownedByQueue, CancellationToken.None);
        await store.SaveAsync(ownedByAgent, CancellationToken.None);
        await store.SaveAsync(ownedByBot, CancellationToken.None);

        var page = await store.ListAsync(Tenant, new ConversationQuery { AssignedAgentId = sharedId }, CancellationToken.None);

        page.Items.Select(c => c.ConversationId).Should().Equal(ownedByAgent.ConversationId);
        page.TotalCount.Should().Be(1);
    }

    // ── VisibleToAgentId (design D4, task 2.2) ───────────────────────────────────

    private static Conversation OfferedTo(EntityId? agentId, ConversationState state = ConversationState.Offered)
    {
        var conversation = NewConversation(ConversationOwner.ForQueue(QueueOne), state);
        if (agentId is { } offeredTo)
            conversation.SetMetadata(ConversationQuery.OfferedToMetadataKey, offeredTo.Value);
        return conversation;
    }

    [Fact]
    public async Task ListAsync_ShouldReturnTheOwnedAndOfferedConversationsOnly_WhenVisibleToAgentIdIsSet()
    {
        var store = await NewStoreAsync();
        var agent = EntityId.From($"visible-{Guid.NewGuid():N}");
        var other = EntityId.From($"other-{Guid.NewGuid():N}");
        var ownedActive = NewConversation(ConversationOwner.ForAgent(agent), ConversationState.Active);
        var ownedClosed = NewConversation(ConversationOwner.ForAgent(agent), ConversationState.Closed);
        var offered = OfferedTo(agent);
        Conversation[] hidden =
        [
            OfferedTo(other),
            OfferedTo(agentId: null),
            // A stale offer stamp on a row that is no longer Offered is no offer.
            OfferedTo(agent, ConversationState.Queued),
            NewConversation(ConversationOwner.ForQueue(agent)),
            NewConversation(ConversationOwner.ForBot(agent), ConversationState.Active),
            NewConversation(ConversationOwner.ForAgent(other), ConversationState.Active),
        ];
        foreach (var conversation in hidden.Concat([ownedActive, ownedClosed, offered]))
            await store.SaveAsync(conversation, CancellationToken.None);

        var page = await store.ListAsync(
            Tenant, new ConversationQuery { VisibleToAgentId = agent, PageSize = 100 }, CancellationToken.None);

        page.Items.Select(c => c.ConversationId).Should().BeEquivalentTo(
            [ownedActive.ConversationId, ownedClosed.ConversationId, offered.ConversationId]);
        page.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task ListAsync_ShouldApplyTheOtherFiltersOnTop_WhenVisibleToAgentIdIsSet()
    {
        var store = await NewStoreAsync();
        var agent = EntityId.From($"visible-{Guid.NewGuid():N}");
        var ownedActive = NewConversation(ConversationOwner.ForAgent(agent), ConversationState.Active);
        var ownedClosed = NewConversation(ConversationOwner.ForAgent(agent), ConversationState.Closed);
        var offered = OfferedTo(agent);
        var othersActive = NewConversation(ConversationOwner.ForAgent(AgentB), ConversationState.Active);
        foreach (var conversation in new[] { ownedActive, ownedClosed, offered, othersActive })
            await store.SaveAsync(conversation, CancellationToken.None);

        var active = await store.ListAsync(
            Tenant, new ConversationQuery { VisibleToAgentId = agent, State = ConversationState.Active }, CancellationToken.None);
        var offers = await store.ListAsync(
            Tenant, new ConversationQuery { VisibleToAgentId = agent, State = ConversationState.Offered }, CancellationToken.None);

        active.Items.Select(c => c.ConversationId).Should().Equal(ownedActive.ConversationId);
        active.TotalCount.Should().Be(1);
        offers.Items.Select(c => c.ConversationId).Should().Equal(offered.ConversationId);
        offers.TotalCount.Should().Be(1);
    }

    // ── HoldsOpenConversationOfContactAsync (design D2, task 2.1) ─────────────────

    private static Conversation OfContact(EntityId contactId, ConversationOwner? owner, ConversationState state)
    {
        var conversation = NewConversation(owner, state);
        return new Conversation
        {
            ConversationId = conversation.ConversationId,
            TenantId = conversation.TenantId,
            ContactId = contactId,
            Channel = conversation.Channel,
            State = state,
            Owner = owner,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = Now.AddMinutes(-30),
        };
    }

    private static Task<bool> HoldsAsync(IConversationStore store, EntityId contactId, EntityId agentId) =>
        store.HoldsOpenConversationOfContactAsync(Tenant, contactId, agentId, CancellationToken.None);

    [Theory]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.Resolved)]
    [InlineData(ConversationState.WrapUp)]
    [InlineData(ConversationState.WaitingForCustomer)]
    [InlineData(ConversationState.OnHold)]
    public async Task HoldsOpenConversationOfContactAsync_ShouldAnswerTrue_WhenTheAgentOwnsANonTerminalConversationOfTheContact(
        ConversationState state)
    {
        var store = await NewStoreAsync();
        var contact = EntityId.From($"contact-{Guid.NewGuid():N}");
        await store.SaveAsync(OfContact(contact, ConversationOwner.ForAgent(AgentA), state), CancellationToken.None);

        (await HoldsAsync(store, contact, AgentA)).Should().BeTrue($"an owned {state} conversation is held (design D2)");
        (await HoldsAsync(store, contact, AgentB)).Should().BeFalse("another agent holds nothing of the contact");
    }

    [Theory]
    [InlineData(ConversationState.Closed)]
    [InlineData(ConversationState.Abandoned)]
    [InlineData(ConversationState.Merged)]
    [InlineData(ConversationState.Spam)]
    public async Task HoldsOpenConversationOfContactAsync_ShouldAnswerFalse_WhenTheAgentOwnsOnlyATerminalConversationOfTheContact(
        ConversationState state)
    {
        var store = await NewStoreAsync();
        var contact = EntityId.From($"contact-{Guid.NewGuid():N}");
        await store.SaveAsync(OfContact(contact, ConversationOwner.ForAgent(AgentA), state), CancellationToken.None);

        (await HoldsAsync(store, contact, AgentA)).Should().BeFalse($"a {state} conversation is terminal, so it is not held");
    }

    [Fact]
    public async Task HoldsOpenConversationOfContactAsync_ShouldAnswerFalse_WhenTheAgentIsOnlyOfferedAConversationOfTheContact()
    {
        var store = await NewStoreAsync();
        var contact = EntityId.From($"contact-{Guid.NewGuid():N}");
        var offered = OfContact(contact, ConversationOwner.ForQueue(QueueOne), ConversationState.Offered);
        offered.SetMetadata("_offeredTo", AgentA.Value); // ConversationOffer.OfferedToKey (Switchboard)
        await store.SaveAsync(offered, CancellationToken.None);

        (await HoldsAsync(store, contact, AgentA)).Should().BeFalse("an offer is not holding (design D2, Q3)");
    }

    [Fact]
    public async Task HoldsOpenConversationOfContactAsync_ShouldAnswerFalse_WhenTheWrapUpIsClosedByTheWrapUpTimeout()
    {
        var store = await NewStoreAsync();
        var contact = EntityId.From($"contact-{Guid.NewGuid():N}");
        var wrapUp = OfContact(contact, ConversationOwner.ForAgent(AgentA), ConversationState.WrapUp);
        await store.SaveAsync(wrapUp, CancellationToken.None);
        (await HoldsAsync(store, contact, AgentA)).Should().BeTrue("a WrapUp counts as held until its timeout closes it");

        // The wrap-up timeout's close: the worker's blank close (TransitionTo Closed + SaveAsync), owner kept.
        var expired = await LoadAsync(store, wrapUp);
        expired.TransitionTo(ConversationState.Closed, Now);
        expired.UpdatedAt = Now;
        await store.SaveAsync(expired, CancellationToken.None);

        (await LoadAsync(store, wrapUp)).Owner.Should().Be(ConversationOwner.ForAgent(AgentA), "the close keeps the owner");
        (await HoldsAsync(store, contact, AgentA)).Should().BeFalse("the closed wrap-up no longer counts");
    }

    [Fact]
    public async Task HoldsOpenConversationOfContactAsync_ShouldAnswerFalse_WhenTheAutonomousWrapUpCloseWins()
    {
        var store = await NewStoreAsync();
        var contact = EntityId.From($"contact-{Guid.NewGuid():N}");
        var wrapUp = OfContact(contact, ConversationOwner.ForAgent(AgentA), ConversationState.WrapUp);
        await store.SaveAsync(wrapUp, CancellationToken.None);

        (await store.TryConditionalCloseWrapUpAsync(Tenant, wrapUp.ConversationId, Now, CancellationToken.None)).Should().BeTrue();

        (await HoldsAsync(store, contact, AgentA)).Should().BeFalse("the wrap-up closed by the compare-and-set no longer counts");
    }

    [Fact]
    public async Task HoldsOpenConversationOfContactAsync_ShouldAnswerFalse_WhenTheConversationWasTransferredAway()
    {
        var store = await NewStoreAsync();
        var contact = EntityId.From($"contact-{Guid.NewGuid():N}");
        var conversation = OfContact(contact, ConversationOwner.ForAgent(AgentA), ConversationState.Active);
        await store.SaveAsync(conversation, CancellationToken.None);

        var toAgent = await LoadAsync(store, conversation);
        toAgent.Owner = ConversationOwner.ForAgent(AgentB);
        await store.SaveAsync(toAgent, CancellationToken.None);
        (await HoldsAsync(store, contact, AgentA)).Should().BeFalse("the allowance ends when the conversation moves to another owner");
        (await HoldsAsync(store, contact, AgentB)).Should().BeTrue("the new owner holds it");

        var toQueue = await LoadAsync(store, conversation);
        toQueue.Owner = ConversationOwner.ForQueue(QueueOne);
        toQueue.State = ConversationState.Queued;
        await store.SaveAsync(toQueue, CancellationToken.None);
        (await HoldsAsync(store, contact, AgentB)).Should().BeFalse("a transfer to a queue leaves no agent holding it");
    }

    [Fact]
    public async Task HoldsOpenConversationOfContactAsync_ShouldAnswerFalse_WhenAQueueOrBotSharesTheAgentsId()
    {
        var store = await NewStoreAsync();
        var contact = EntityId.From($"contact-{Guid.NewGuid():N}");
        var sharedId = EntityId.From($"shared-{Guid.NewGuid():N}");
        await store.SaveAsync(OfContact(contact, ConversationOwner.ForQueue(sharedId), ConversationState.Queued), CancellationToken.None);
        await store.SaveAsync(OfContact(contact, ConversationOwner.ForBot(sharedId), ConversationState.Active), CancellationToken.None);

        (await HoldsAsync(store, contact, sharedId)).Should().BeFalse("ownership is the owner's kind and id together");
    }

    [Fact]
    public async Task HoldsOpenConversationOfContactAsync_ShouldAnswerFalse_WhenTheHeldConversationIsOfAnotherContactOrTenant()
    {
        var store = await NewStoreAsync();
        var contact = EntityId.From($"contact-{Guid.NewGuid():N}");
        var otherContact = EntityId.From($"contact-{Guid.NewGuid():N}");
        await store.SaveAsync(OfContact(otherContact, ConversationOwner.ForAgent(AgentA), ConversationState.Active), CancellationToken.None);
        var otherTenant = new Conversation
        {
            ConversationId = EntityId.From($"c-{Guid.NewGuid():N}"),
            TenantId = new TenantId("t-ownership-other"),
            ContactId = contact,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(AgentA),
            CreatedAt = Now.AddHours(-1),
        };
        await store.SaveAsync(otherTenant, CancellationToken.None);

        (await HoldsAsync(store, contact, AgentA)).Should().BeFalse("holding is scoped to the contact and the tenant");
        (await HoldsAsync(store, otherContact, AgentA)).Should().BeTrue();
    }

    /// <summary>A copy that shares nothing with the stored row, as a Postgres read builds one.</summary>
    private static Conversation Detach(Conversation c)
    {
        var copy = new Conversation
        {
            ConversationId = c.ConversationId,
            TenantId = c.TenantId,
            ContactId = c.ContactId,
            Channel = c.Channel,
            Owner = c.Owner,
            State = c.State,
            CaseId = c.CaseId,
            CreatedAt = c.CreatedAt,
            ClosedAt = c.ClosedAt,
            UpdatedAt = c.UpdatedAt,
            VoiceLinkedId = c.VoiceLinkedId,
            QueuePriority = c.QueuePriority,
            LastMessageAt = c.LastMessageAt,
            QueueId = c.QueueId,
        };
        foreach (var kv in c.Metadata)
            copy.SetMetadata(kv.Key, kv.Value);
        return copy;
    }
}
