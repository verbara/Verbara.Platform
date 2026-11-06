using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// conversations-return-to-last-agent task 1.3 (design D4, ruling Q3, audit 2026-11-06 F4), run against every store:
/// <see cref="IConversationStore.FindLastAgentOwnerAsync"/> names the agent owner of the contact's conversation with the
/// latest <c>last_message_at</c> at or after <c>since</c>, on any channel, open or closed, <c>Merged</c> and <c>Spam</c>
/// excluded. Each case is built so a plausible wrong query fails it: ordered by <c>created_at</c>, a terminal-state
/// allow-list, filtered or ordered by <c>closed_at</c>.
/// </summary>
/// <remarks>
/// Linked into Verbara.Platform.Storage.Postgres.Tests. <c>last_message_at</c> is seeded through the insert, which both
/// stores honour (<c>COALESCE(@LastMessageAt, @CreatedAt)</c>), so the same cases run on both; the whole-row save never
/// writes it on an update. <see cref="Now"/> is whole seconds, so Postgres' microsecond resolution keeps every instant.
/// </remarks>
public abstract class LastAgentOwnerStoreContract
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Since72 = Now.AddHours(-72);
    private static readonly EntityId AgentA = EntityId.From("agent-a");
    private static readonly EntityId AgentB = EntityId.From("agent-b");
    private static readonly EntityId QueueOne = EntityId.From("queue-one");

    protected static readonly TenantId Tenant = new("t-last-agent");

    /// <summary>A new, empty store.</summary>
    protected abstract Task<IConversationStore> NewStoreAsync();

    private static EntityId NewContact() => EntityId.From($"contact-{Guid.NewGuid():N}");

    private static Conversation Row(
        EntityId contact, ConversationOwner? owner, ConversationState state, DateTimeOffset createdAt, DateTimeOffset? lastMessageAt,
        DateTimeOffset? closedAt = null, ChannelType channel = ChannelType.WhatsApp, TenantId? tenant = null) =>
        new()
        {
            ConversationId = EntityId.From($"c-{Guid.NewGuid():N}"),
            TenantId = tenant ?? Tenant,
            ContactId = contact,
            Channel = channel,
            State = state,
            Owner = owner,
            CreatedAt = createdAt,
            LastMessageAt = lastMessageAt,
            ClosedAt = closedAt,
        };

    private static async Task SaveAsync(IConversationStore store, params Conversation[] rows)
    {
        foreach (var row in rows)
            await store.SaveAsync(row, CancellationToken.None);
    }

    private static Task<EntityId?> FindAsync(IConversationStore store, EntityId contact, DateTimeOffset since) =>
        store.FindLastAgentOwnerAsync(Tenant, contact, since, CancellationToken.None);

    [Fact]
    public async Task FindLastAgentOwnerAsync_ShouldNameTheLatestLastMessage_WhenALaterCreatedRowSpokeEarlier()
    {
        var store = await NewStoreAsync();
        var contact = NewContact();
        // X created first, last message an hour ago (A); Y created later, last message two days ago (B).
        var x = Row(contact, ConversationOwner.ForAgent(AgentA), ConversationState.Closed,
            Now.AddHours(-60), Now.AddHours(-1), closedAt: Now.AddMinutes(-30));
        var y = Row(contact, ConversationOwner.ForAgent(AgentB), ConversationState.Closed,
            Now.AddHours(-50), Now.AddHours(-48), closedAt: Now.AddMinutes(-10));
        await SaveAsync(store, x, y);

        (await FindAsync(store, contact, Since72)).Should().Be(AgentA, "recency is the last message, not creation nor close");
    }

    [Fact]
    public async Task FindLastAgentOwnerAsync_ShouldNameTheOpenRowsAgent_WhenAnOpenRowOnAnotherChannelIsNewer()
    {
        var store = await NewStoreAsync();
        var contact = NewContact();
        var open = Row(contact, ConversationOwner.ForAgent(AgentA), ConversationState.Active,
            Now.AddHours(-2), Now.AddMinutes(-10), channel: ChannelType.WebChat);
        var closed = Row(contact, ConversationOwner.ForAgent(AgentB), ConversationState.Closed,
            Now.AddHours(-3), Now.AddHours(-1), closedAt: Now.AddMinutes(-50));
        await SaveAsync(store, open, closed);

        (await FindAsync(store, contact, Since72)).Should().Be(AgentA, "an open agent-owned conversation counts (ruling Q3)");
    }

    [Fact]
    public async Task FindLastAgentOwnerAsync_ShouldNameNobody_WhenTheLastMessageIsOutsideTheWindowAndTheCloseInside()
    {
        var store = await NewStoreAsync();
        var contact = NewContact();
        await SaveAsync(store, Row(contact, ConversationOwner.ForAgent(AgentA), ConversationState.Closed,
            Now.AddHours(-80), Now.AddHours(-73), closedAt: Now.AddHours(-1)));

        (await FindAsync(store, contact, Since72)).Should().BeNull("the window counts from the last message, never the close");
    }

    [Fact]
    public async Task FindLastAgentOwnerAsync_ShouldFollowSince_WhenTheCallerClampsTheWindow()
    {
        var store = await NewStoreAsync();
        var contact = NewContact();
        var returned = Row(contact, ConversationOwner.ForAgent(AgentA), ConversationState.Closed,
            Now.AddHours(-110), Now.AddHours(-100), closedAt: Now.AddMinutes(-1));
        returned.SetMetadata("_closedReason", "customer-returned");
        await SaveAsync(store, returned);

        (await FindAsync(store, contact, Now.AddHours(-120))).Should().Be(AgentA, "a 120 h clamp admits a row 100 h old");
        (await FindAsync(store, contact, Since72)).Should().BeNull("a 72 h window does not");
    }

    [Fact]
    public async Task FindLastAgentOwnerAsync_ShouldCountAVoiceCallFromItsCreation_WhenItWasStoredWithNoLastMessage()
    {
        var store = await NewStoreAsync();
        var contact = NewContact();
        var call = Row(contact, ConversationOwner.ForAgent(AgentA), ConversationState.Closed,
            Now.AddHours(-73), lastMessageAt: null, closedAt: Now.AddHours(-1), channel: ChannelType.Voice);
        await SaveAsync(store, call);

        (await FindAsync(store, contact, Since72)).Should().BeNull("a voice call's instant is its start, 73 h ago");
        (await store.GetByIdAsync(Tenant, call.ConversationId, CancellationToken.None))!.LastMessageAt
            .Should().Be(Now.AddHours(-73), "the store writes created_at as the voice call's last_message_at");
    }

    [Fact]
    public async Task FindLastAgentOwnerAsync_ShouldAdmitTheEdgeAndRejectJustBeforeIt_WhenTheLastMessageIsAtTheWindowEdge()
    {
        var store = await NewStoreAsync();
        var atEdge = NewContact();
        var beforeEdge = NewContact();
        // One microsecond before the edge: the smallest step Postgres timestamptz keeps (a 100 ns tick rounds away).
        await SaveAsync(store,
            Row(atEdge, ConversationOwner.ForAgent(AgentA), ConversationState.Closed, Since72.AddHours(-1), Since72, Now),
            Row(beforeEdge, ConversationOwner.ForAgent(AgentA), ConversationState.Closed, Since72.AddHours(-1),
                Since72.AddTicks(-10), Now));

        (await FindAsync(store, atEdge, Since72)).Should().Be(AgentA, "the edge is inside the window");
        (await FindAsync(store, beforeEdge, Since72)).Should().BeNull("just before the edge is outside");
    }

    [Theory]
    [InlineData(73, false)]
    [InlineData(71, true)]
    public async Task FindLastAgentOwnerAsync_ShouldFollowTheLastMessage_WhenACustomerReturnedCloseCameAfterTheInactivityCutoff(
        int hoursSinceLastMessage, bool qualifies)
    {
        // Audit 2026-11-06 F4: a reply between the inactivity cutoff and the next sweep closes the Resolved row as
        // customer-returned without moving last_message_at; the row qualifies only while that instant is in the window.
        var store = await NewStoreAsync();
        var contact = NewContact();
        var returned = Row(contact, ConversationOwner.ForAgent(AgentA), ConversationState.Closed,
            Now.AddHours(-hoursSinceLastMessage - 1), Now.AddHours(-hoursSinceLastMessage), closedAt: Now.AddSeconds(-5));
        returned.SetMetadata("_closedReason", "customer-returned");
        await SaveAsync(store, returned);

        var owner = await FindAsync(store, contact, Since72);

        if (qualifies)
            owner.Should().Be(AgentA);
        else
            owner.Should().BeNull();
    }

    [Theory]
    [InlineData(ConversationState.Merged)]
    [InlineData(ConversationState.Spam)]
    public async Task FindLastAgentOwnerAsync_ShouldSkipTheRow_WhenItIsMergedOrSpam(ConversationState excluded)
    {
        var store = await NewStoreAsync();
        var contact = NewContact();
        await SaveAsync(store,
            Row(contact, ConversationOwner.ForAgent(AgentA), excluded, Now.AddHours(-3), Now.AddHours(-1), Now.AddMinutes(-30)),
            Row(contact, ConversationOwner.ForAgent(AgentB), ConversationState.Closed, Now.AddHours(-30), Now.AddHours(-20), Now.AddHours(-19)));

        (await FindAsync(store, contact, Since72)).Should().Be(AgentB, "{0} never names a previous agent", excluded);
    }

    [Fact]
    public async Task FindLastAgentOwnerAsync_ShouldSkipQueueOwnedAndOwnerlessRows_WhenTheyAreNewer()
    {
        var store = await NewStoreAsync();
        var contact = NewContact();
        await SaveAsync(store,
            Row(contact, ConversationOwner.ForQueue(QueueOne), ConversationState.Queued, Now.AddMinutes(-5), Now.AddMinutes(-5)),
            Row(contact, owner: null, ConversationState.Queued, Now.AddMinutes(-4), Now.AddMinutes(-4)),
            Row(contact, ConversationOwner.ForAgent(AgentB), ConversationState.Closed, Now.AddHours(-30), Now.AddHours(-20), Now.AddHours(-19)));

        (await FindAsync(store, contact, Since72)).Should().Be(AgentB, "only an agent-owned row names an agent");
    }

    [Fact]
    public async Task FindLastAgentOwnerAsync_ShouldNameNobody_WhenTheOnlyRowBelongsToAnotherTenant()
    {
        var store = await NewStoreAsync();
        var contact = NewContact();
        await SaveAsync(store, Row(contact, ConversationOwner.ForAgent(AgentA), ConversationState.Closed,
            Now.AddHours(-3), Now.AddHours(-1), Now.AddMinutes(-30), tenant: new TenantId("t-last-agent-other")));

        (await FindAsync(store, contact, Since72)).Should().BeNull("the lookup is tenant-scoped");
    }
}
