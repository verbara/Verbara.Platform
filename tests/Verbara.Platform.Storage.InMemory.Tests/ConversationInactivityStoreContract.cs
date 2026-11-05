using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// conversations-close-after-inactivity task 1.5 (design D3, D4, D7, D8, D9, D12): the store contract of the
/// inactivity close and of the claims, run against every store. The list reads the in-scope idle rows; the
/// conditional close wins once and loses to any change of state, owner or clock since the read, decides the
/// backlog mark in the statement, and never goes through <see cref="Conversation.TransitionTo"/> (an <c>Active</c>
/// row closes although the transition table refuses <c>Active → Closed</c>); the claims advance the clock
/// monotonically and refuse the rows they must not touch.
/// </summary>
/// <remarks>Linked into Verbara.Platform.Storage.Postgres.Tests.</remarks>
public abstract class ConversationInactivityStoreContract
{
    protected static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = Now.AddHours(-72);
    private static readonly DateTimeOffset BacklogCutoff = Now.AddHours(-73);
    private static readonly EntityId AgentA = EntityId.From("agent-a");
    private static readonly EntityId AgentB = EntityId.From("agent-b");

    protected static readonly TenantId Tenant = new("t-inactivity");

    /// <summary>A new, empty store.</summary>
    protected abstract Task<IConversationStore> NewStoreAsync();

    private static async Task<Conversation> SeedAsync(
        IConversationStore store, ConversationState state, DateTimeOffset clock, ChannelType channel = ChannelType.WhatsApp,
        ConversationOwner? owner = null)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"c-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = channel,
            State = state,
            Owner = owner ?? ConversationOwner.ForAgent(AgentA),
            CreatedAt = clock.AddHours(-1),
            LastMessageAt = clock,
        };
        await store.SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    private static InactivityCloseRequest ReadOf(Conversation read, DateTimeOffset? cutoff = null) =>
        new(read.ConversationId, read.State, read.Owner, cutoff ?? Cutoff, BacklogCutoff, Now);

    private static async Task<Conversation> LoadAsync(IConversationStore store, Conversation conversation) =>
        (await store.GetByIdAsync(Tenant, conversation.ConversationId, CancellationToken.None))!;

    // ── FindOpenBySessionAddressAsync (webchat-visitor-identity-two-levels, design D9) ─────────────

    [Fact]
    public async Task FindOpenBySessionAddressAsync_ShouldReturnTheNewestOpenRowOfThatContactAndSession_WhenOthersExist()
    {
        var store = await NewStoreAsync();
        var contactId = EntityId.From($"contact-{Guid.NewGuid():N}");
        const string Session = "webchat-11111111111111111111111111111111";
        var address = new ChannelAddress(ChannelType.WebChat, Session);

        async Task<Conversation> SeedOfAsync(
            ConversationState state, DateTimeOffset createdAt, string? recorded = Session, EntityId? contact = null, ChannelType channel = ChannelType.WebChat)
        {
            var conversation = new Conversation
            {
                ConversationId = EntityId.From($"c-{Guid.NewGuid():N}"),
                TenantId = Tenant,
                ContactId = contact ?? contactId,
                Channel = channel,
                State = state,
                Owner = ConversationOwner.ForAgent(AgentA),
                CreatedAt = createdAt,
                LastMessageAt = createdAt,
            };
            if (recorded is not null)
                conversation.SetMetadata(WebChatConversationMetadata.SessionAddressKey, recorded);
            await store.SaveAsync(conversation, CancellationToken.None);
            return conversation;
        }

        (await store.FindOpenBySessionAddressAsync(Tenant, contactId, address, CancellationToken.None)).Should().BeNull();

        await SeedOfAsync(ConversationState.Active, Now.AddHours(-3));
        var newest = await SeedOfAsync(ConversationState.Resolved, Now.AddHours(-2));
        await SeedOfAsync(ConversationState.Closed, Now.AddHours(-1));
        await SeedOfAsync(ConversationState.Abandoned, Now.AddHours(-1));
        await SeedOfAsync(ConversationState.Active, Now, recorded: "webchat-22222222222222222222222222222222");
        await SeedOfAsync(ConversationState.Active, Now, recorded: null);
        await SeedOfAsync(ConversationState.Active, Now, contact: EntityId.From($"contact-{Guid.NewGuid():N}"));
        await SeedOfAsync(ConversationState.Active, Now, channel: ChannelType.WhatsApp);

        var found = await store.FindOpenBySessionAddressAsync(Tenant, contactId, address, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ConversationId.Should().Be(newest.ConversationId);
        (await store.FindOpenBySessionAddressAsync(new TenantId("t-other"), contactId, address, CancellationToken.None)).Should().BeNull();
    }

    // ── CountOpenByContactAsync (webchat-visitor-identity-two-levels, design D17) ─────────────

    [Fact]
    public async Task CountOpenByContactAsync_ShouldCountOnlyThatContactsNonTerminalRowsOnTheChannel_WhenOthersExist()
    {
        var store = await NewStoreAsync();
        var contactId = EntityId.From($"contact-{Guid.NewGuid():N}");

        async Task SeedOfContactAsync(ConversationState state, ChannelType channel = ChannelType.WebChat) =>
            await store.SaveAsync(
                new Conversation
                {
                    ConversationId = EntityId.From($"c-{Guid.NewGuid():N}"),
                    TenantId = Tenant,
                    ContactId = contactId,
                    Channel = channel,
                    State = state,
                    Owner = ConversationOwner.ForAgent(AgentA),
                    CreatedAt = Now,
                    LastMessageAt = Now,
                },
                CancellationToken.None);

        await SeedOfContactAsync(ConversationState.Queued);
        await SeedOfContactAsync(ConversationState.Active);
        await SeedOfContactAsync(ConversationState.Resolved);
        await SeedOfContactAsync(ConversationState.Closed);
        await SeedOfContactAsync(ConversationState.Abandoned);
        await SeedOfContactAsync(ConversationState.Active, ChannelType.WhatsApp);
        await SeedAsync(store, ConversationState.Active, Now, ChannelType.WebChat);

        (await store.CountOpenByContactAsync(Tenant, contactId, ChannelType.WebChat, CancellationToken.None)).Should().Be(3);
        (await store.CountOpenByContactAsync(Tenant, EntityId.New(), ChannelType.WebChat, CancellationToken.None)).Should().Be(0);
    }

    // ── ListInactiveAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListInactiveAsync_ShouldReturnOnlyIdleInScopeDigitalRowsOldestFirst_WhenLimited()
    {
        var store = await NewStoreAsync();
        var oldest = await SeedAsync(store, ConversationState.OnHold, Now.AddHours(-200));
        var older = await SeedAsync(store, ConversationState.Active, Now.AddHours(-150));
        var old = await SeedAsync(store, ConversationState.Resolved, Now.AddHours(-100));
        await SeedAsync(store, ConversationState.WaitingForCustomer, Now.AddHours(-80));
        await SeedAsync(store, ConversationState.Active, Now.AddHours(-71));                       // inside the window
        await SeedAsync(store, ConversationState.Snoozed, Now.AddHours(-300));                     // out of scope
        await SeedAsync(store, ConversationState.WrapUp, Now.AddHours(-300));                      // out of scope
        await SeedAsync(store, ConversationState.Active, Now.AddHours(-300), ChannelType.Voice);   // voice

        var page = await store.ListInactiveAsync(
            Tenant, ConversationStateMachine.InactivityCloseStates, Cutoff, 3, CancellationToken.None);

        page.Select(c => c.ConversationId).Should().Equal(
            [oldest.ConversationId, older.ConversationId, old.ConversationId],
            "in-scope, non-voice, older than the cutoff, oldest first, at most the limit");
        page[0].LastMessageAt.Should().Be(Now.AddHours(-200));
    }

    // ── TryCloseInactiveAsync ──────────────────────────────────────────────────

    [Theory]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.OnHold)]
    [InlineData(ConversationState.Consulting)]
    [InlineData(ConversationState.WaitingForCustomer)]
    [InlineData(ConversationState.Resolved)]
    public async Task TryCloseInactiveAsync_ShouldWinOnceAndKeepTheOwner_WhenNothingChangedSinceTheRead(ConversationState state)
    {
        var store = await NewStoreAsync();
        var read = await SeedAsync(store, state, Now.AddHours(-72.5));

        var first = await store.TryCloseInactiveAsync(Tenant, ReadOf(read), CancellationToken.None);
        var second = await store.TryCloseInactiveAsync(Tenant, ReadOf(read), CancellationToken.None);

        first.Should().NotBeNull("the first close wins");
        first!.Reason.Should().Be(ConversationCloseReasons.Inactivity);
        first.LastMessageAt.Should().Be(Now.AddHours(-72.5));
        second.Should().BeNull("the row left {0}: the second close matches nothing", state);
        var stored = await LoadAsync(store, read);
        stored.State.Should().Be(ConversationState.Closed);
        stored.ClosedAt.Should().Be(Now);
        stored.UpdatedAt.Should().Be(Now);
        stored.Owner.Should().Be(ConversationOwner.ForAgent(AgentA), "the close keeps the owner (decision 6 reads it)");
        stored.Metadata.Should().Contain(ConversationCloseReasons.MetadataKey, ConversationCloseReasons.Inactivity);
    }

    [Fact]
    public async Task TryCloseInactiveAsync_ShouldLose_WhenTheStateChangedSinceTheRead()
    {
        var store = await NewStoreAsync();
        var read = await SeedAsync(store, ConversationState.Active, Now.AddHours(-80));
        var request = ReadOf(read);
        var moved = await LoadAsync(store, read);
        moved.State = ConversationState.OnHold;
        await store.SaveAsync(moved, CancellationToken.None);

        (await store.TryCloseInactiveAsync(Tenant, request, CancellationToken.None)).Should().BeNull();
        (await LoadAsync(store, read)).State.Should().Be(ConversationState.OnHold);
    }

    [Fact]
    public async Task TryCloseInactiveAsync_ShouldLose_WhenTheOwnerChangedSinceTheRead()
    {
        var store = await NewStoreAsync();
        var read = await SeedAsync(store, ConversationState.Active, Now.AddHours(-80));
        var request = ReadOf(read);
        var transferred = await LoadAsync(store, read);
        transferred.Owner = ConversationOwner.ForAgent(AgentB);
        await store.SaveAsync(transferred, CancellationToken.None);

        (await store.TryCloseInactiveAsync(Tenant, request, CancellationToken.None)).Should().BeNull(
            "a transfer committed between the read and the close makes the close a no-op");
        var stored = await LoadAsync(store, read);
        stored.State.Should().Be(ConversationState.Active);
        stored.Owner.Should().Be(ConversationOwner.ForAgent(AgentB));
    }

    [Fact]
    public async Task TryCloseInactiveAsync_ShouldLose_WhenTheClockMovedPastTheCutoffSinceTheRead()
    {
        var store = await NewStoreAsync();
        var read = await SeedAsync(store, ConversationState.Active, Now.AddHours(-80));
        var request = ReadOf(read);
        (await store.TryClaimForInboundAsync(Tenant, read.ConversationId, Now.AddMinutes(-1), CancellationToken.None))
            .Should().BeTrue();

        (await store.TryCloseInactiveAsync(Tenant, request, CancellationToken.None)).Should().BeNull(
            "a message stored between list and close keeps the chat open");
        var stored = await LoadAsync(store, read);
        stored.State.Should().Be(ConversationState.Active);
        stored.ClosedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(ConversationState.Snoozed)]
    [InlineData(ConversationState.WrapUp)]
    public async Task TryCloseInactiveAsync_ShouldLoseAndWriteNothing_WhenTheReadStateIsOutOfScope(ConversationState state)
    {
        var store = await NewStoreAsync();
        var read = await SeedAsync(store, state, Now.AddHours(-300));

        (await store.TryCloseInactiveAsync(Tenant, ReadOf(read), CancellationToken.None)).Should().BeNull(
            "{0} fails CanCloseForInactivity (D3)", state);
        var stored = await LoadAsync(store, read);
        stored.State.Should().Be(state);
        stored.ClosedAt.Should().BeNull();
        stored.UpdatedAt.Should().BeNull();
        stored.Metadata.Should().NotContainKey(ConversationCloseReasons.MetadataKey);
    }

    [Fact]
    public async Task TryCloseInactiveAsync_ShouldMarkTheBacklog_WhenTheClockIsOlderThanTheBacklogCutoff()
    {
        var store = await NewStoreAsync();
        var backlog = await SeedAsync(store, ConversationState.Active, Now.AddHours(-200));
        var steady = await SeedAsync(store, ConversationState.Active, Now.AddHours(-72.5));

        (await store.TryCloseInactiveAsync(Tenant, ReadOf(backlog), CancellationToken.None))!
            .Reason.Should().Be(ConversationCloseReasons.InactivityBacklog);
        (await store.TryCloseInactiveAsync(Tenant, ReadOf(steady), CancellationToken.None))!
            .Reason.Should().Be(ConversationCloseReasons.Inactivity);
        (await LoadAsync(store, backlog)).Metadata.Should().Contain(
            ConversationCloseReasons.MetadataKey, ConversationCloseReasons.InactivityBacklog);
        (await LoadAsync(store, steady)).Metadata.Should().Contain(
            ConversationCloseReasons.MetadataKey, ConversationCloseReasons.Inactivity);
    }

    // ── TryClaimForInboundAsync ────────────────────────────────────────────────

    [Fact]
    public async Task TryClaimForInboundAsync_ShouldAdvanceTheClockMonotonically_WhenTheRowIsOpen()
    {
        var store = await NewStoreAsync();
        var conversation = await SeedAsync(store, ConversationState.WrapUp, Now.AddHours(-10));

        (await store.TryClaimForInboundAsync(Tenant, conversation.ConversationId, Now, CancellationToken.None)).Should().BeTrue();
        (await LoadAsync(store, conversation)).LastMessageAt.Should().Be(Now);

        (await store.TryClaimForInboundAsync(Tenant, conversation.ConversationId, Now.AddHours(-5), CancellationToken.None))
            .Should().BeTrue("the row is still open");
        (await LoadAsync(store, conversation)).LastMessageAt.Should().Be(Now, "the clock never moves back");
        (await LoadAsync(store, conversation)).UpdatedAt.Should().BeNull("the claim does not touch updated_at (D2)");
    }

    [Theory]
    [InlineData(ConversationState.Closed)]
    [InlineData(ConversationState.Abandoned)]
    [InlineData(ConversationState.Resolved)]
    public async Task TryClaimForInboundAsync_ShouldReturnFalseAndWriteNothing_WhenTheRowIsTerminalOrResolved(ConversationState state)
    {
        var store = await NewStoreAsync();
        var conversation = await SeedAsync(store, state, Now.AddHours(-10));

        (await store.TryClaimForInboundAsync(Tenant, conversation.ConversationId, Now, CancellationToken.None)).Should().BeFalse(
            "a terminal row is never claimed, and a Resolved row is D8's, never the claim's");
        (await LoadAsync(store, conversation)).LastMessageAt.Should().Be(Now.AddHours(-10));
    }

    [Fact]
    public async Task TryClaimForInboundAsync_ShouldReturnFalse_WhenTheRowIsGone()
    {
        var store = await NewStoreAsync();

        (await store.TryClaimForInboundAsync(Tenant, EntityId.From("c-missing"), Now, CancellationToken.None)).Should().BeFalse();
    }

    // ── TryCloseResolvedForReplyAsync ──────────────────────────────────────────

    [Fact]
    public async Task TryCloseResolvedForReplyAsync_ShouldCloseOnceAsCustomerReturned_WhenTheRowIsResolved()
    {
        var store = await NewStoreAsync();
        var resolved = await SeedAsync(store, ConversationState.Resolved, Now.AddHours(-10));

        (await store.TryCloseResolvedForReplyAsync(Tenant, resolved.ConversationId, Now, CancellationToken.None)).Should().BeTrue();
        (await store.TryCloseResolvedForReplyAsync(Tenant, resolved.ConversationId, Now, CancellationToken.None)).Should().BeFalse(
            "only one delivery wins the return close");

        var stored = await LoadAsync(store, resolved);
        stored.State.Should().Be(ConversationState.Closed);
        stored.ClosedAt.Should().Be(Now, "closed_at = now shields the row from the purge (D7)");
        stored.Owner.Should().Be(ConversationOwner.ForAgent(AgentA));
        stored.Metadata.Should().Contain(ConversationCloseReasons.MetadataKey, ConversationCloseReasons.CustomerReturned);
    }

    [Fact]
    public async Task TryCloseResolvedForReplyAsync_ShouldReturnFalse_WhenTheRowIsNotResolved()
    {
        var store = await NewStoreAsync();
        var active = await SeedAsync(store, ConversationState.Active, Now.AddHours(-10));

        (await store.TryCloseResolvedForReplyAsync(Tenant, active.ConversationId, Now, CancellationToken.None)).Should().BeFalse();
        (await LoadAsync(store, active)).State.Should().Be(ConversationState.Active);
    }

    // ── TryClaimForSendAsync ───────────────────────────────────────────────────

    [Theory]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.OnHold)]
    [InlineData(ConversationState.Consulting)]
    public async Task TryClaimForSendAsync_ShouldAdvanceTheClock_WhenSendableAndAsked(ConversationState state)
    {
        var store = await NewStoreAsync();
        var conversation = await SeedAsync(store, state, Now.AddHours(-10));

        (await store.TryClaimForSendAsync(Tenant, conversation.ConversationId, Now, advanceClock: true, CancellationToken.None))
            .Should().BeTrue();
        (await LoadAsync(store, conversation)).LastMessageAt.Should().Be(Now);
    }

    [Fact]
    public async Task TryClaimForSendAsync_ShouldLeaveTheClock_WhenNotAskedToAdvanceIt()
    {
        var store = await NewStoreAsync();
        var conversation = await SeedAsync(store, ConversationState.Active, Now.AddHours(-10));

        (await store.TryClaimForSendAsync(Tenant, conversation.ConversationId, Now, advanceClock: false, CancellationToken.None))
            .Should().BeTrue();
        (await LoadAsync(store, conversation)).LastMessageAt.Should().Be(Now.AddHours(-10));
    }

    [Theory]
    [InlineData(ConversationState.WaitingForCustomer)]
    [InlineData(ConversationState.WrapUp)]
    [InlineData(ConversationState.Resolved)]
    [InlineData(ConversationState.Closed)]
    public async Task TryClaimForSendAsync_ShouldReturnFalse_WhenTheRowIsNotSendable(ConversationState state)
    {
        var store = await NewStoreAsync();
        var conversation = await SeedAsync(store, state, Now.AddHours(-10));

        (await store.TryClaimForSendAsync(Tenant, conversation.ConversationId, Now, advanceClock: true, CancellationToken.None))
            .Should().BeFalse();
        (await LoadAsync(store, conversation)).LastMessageAt.Should().Be(Now.AddHours(-10));
    }
}
