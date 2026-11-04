using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// contact-history-opens-by-assignment task 2.3 (design D5, D6, D8, D9, D18), run against every store:
/// <see cref="IConversationStore.ListContactSummariesAsync"/> lists a contact's conversations in every state, newest
/// first, joins the queue name, the agent owner's display name and the disposition (the correction's leaf when one
/// exists), and picks the newest activity message, never a <c>System</c> note or a failed send. With an owner
/// restriction it lists and counts only that agent's own conversations.
/// </summary>
/// <remarks>Linked into Verbara.Platform.Storage.Postgres.Tests.</remarks>
public abstract class ContactConversationSummaryStoreContract
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly EntityId Contact = EntityId.From("contact-k");
    private static readonly EntityId AgentA = EntityId.From("agent-a");
    private static readonly EntityId AgentB = EntityId.From("agent-b");
    private static readonly EntityId QueueSupport = EntityId.From("queue-support");

    protected static readonly TenantId Tenant = new("t-summary");
    private static readonly TenantId OtherTenant = new("t-summary-other");

    /// <summary>
    /// The stores one summary reads, over one empty database. <paramref name="InsertAgent"/> creates an agent the way
    /// the store's own creator does (the Postgres <see cref="IAgentStore.SaveAsync"/> updates only).
    /// </summary>
    protected sealed record World(
        IConversationStore Conversations,
        IMessageStore Messages,
        IQueueStore Queues,
        IAgentStore Agents,
        Func<Agent, Task> InsertAgent,
        ITypificationSubmissionStore Submissions,
        ITypificationSubmissionCorrectionStore Corrections);

    /// <summary>A new, empty world.</summary>
    protected abstract Task<World> NewWorldAsync();

    private static async Task<Conversation> SeedAsync(
        World world,
        string id,
        ConversationState state,
        DateTimeOffset createdAt,
        params ConversationOwner?[] owners)
    {
        // Each owner is one whole-row save, in order: the store records the last queue among them (design D7).
        var conversation = new Conversation
        {
            ConversationId = EntityId.From(id),
            TenantId = Tenant,
            ContactId = Contact,
            Channel = ChannelType.WhatsApp,
            State = state,
            Owner = owners.Length == 0 ? null : owners[0],
            CreatedAt = createdAt,
        };
        await world.Conversations.SaveAsync(conversation, CancellationToken.None);
        foreach (var owner in owners.Skip(1))
        {
            var loaded = (await world.Conversations.GetByIdAsync(Tenant, conversation.ConversationId, CancellationToken.None))!;
            loaded.Owner = owner;
            await world.Conversations.SaveAsync(loaded, CancellationToken.None);
        }

        return (await world.Conversations.GetByIdAsync(Tenant, conversation.ConversationId, CancellationToken.None))!;
    }

    private static Task SeedMessageAsync(
        World world,
        Conversation conversation,
        string id,
        MessageDirection direction,
        DateTimeOffset createdAt,
        MessageBlock? block = null,
        MessageDeliveryStatus status = MessageDeliveryStatus.Delivered) =>
        world.Messages.SaveAsync(new Message
        {
            MessageId = EntityId.From(id),
            ConversationId = conversation.ConversationId,
            TenantId = conversation.TenantId,
            Direction = direction,
            Channel = conversation.Channel,
            Content = new MessageEnvelope([block ?? new TextBlock(id)]),
            DeliveryStatus = status,
            CreatedAt = createdAt,
        }, CancellationToken.None);

    private static Task SeedQueueAsync(World world, EntityId queueId, string name) =>
        world.Queues.SaveAsync(new Queue { QueueId = queueId, TenantId = Tenant, Name = name, CreatedAt = T0 }, CancellationToken.None);

    private static Task SeedAgentAsync(World world, EntityId agentId, string displayName) =>
        world.InsertAgent(new Agent
        {
            AgentId = agentId,
            TenantId = Tenant,
            UserId = EntityId.From($"user-of-{agentId.Value}"),
            DisplayName = displayName,
            State = AgentState.Available,
            CreatedAt = T0,
        });

    private static Task<bool> SeedSubmissionAsync(World world, Conversation conversation, string leaf, string schema, int version) =>
        world.Submissions.TryInsertAsync(new TypificationSubmission
        {
            TenantId = Tenant,
            ConversationId = conversation.ConversationId,
            AgentId = AgentA,
            SchemaId = EntityId.From(schema),
            SchemaVersion = version,
            SelectedNodePath = [EntityId.From(leaf)],
            LeafNodeId = EntityId.From(leaf),
            FieldValues = new Dictionary<string, string>(),
            CompletedAt = T0.AddHours(2),
        }, null, CancellationToken.None);

    private static async Task<PagedResult<ContactConversationSummary>> ListAsync(
        World world, EntityId? ownedBy = null, int page = 1, int pageSize = 25) =>
        await world.Conversations.ListContactSummariesAsync(Tenant, Contact, ownedBy, page, pageSize, CancellationToken.None);

    private static IEnumerable<string> Ids(PagedResult<ContactConversationSummary> result) =>
        result.Items.Select(s => s.Conversation.ConversationId.Value);

    [Fact]
    public async Task ListContactSummariesAsync_ShouldListEveryStateNewestFirst_WhenTheContactHasConversationsInManyStates()
    {
        var world = await NewWorldAsync();
        await SeedAsync(world, "c-active", ConversationState.Active, T0.AddHours(-3), ConversationOwner.ForAgent(AgentA));
        await SeedAsync(world, "c-closed", ConversationState.Closed, T0.AddHours(-2), ConversationOwner.ForAgent(AgentA));
        await SeedAsync(world, "c-abandoned", ConversationState.Abandoned, T0.AddHours(-1), ConversationOwner.ForQueue(QueueSupport));
        await SeedAsync(world, "c-tie-a", ConversationState.Spam, T0.AddHours(-4));
        await SeedAsync(world, "c-tie-b", ConversationState.Merged, T0.AddHours(-4));
        // Another contact and another tenant are never listed.
        await world.Conversations.SaveAsync(new Conversation
        {
            ConversationId = EntityId.From("c-other-contact"), TenantId = Tenant, ContactId = EntityId.From("contact-other"),
            Channel = ChannelType.WhatsApp, State = ConversationState.Active, CreatedAt = T0,
        }, CancellationToken.None);
        await world.Conversations.SaveAsync(new Conversation
        {
            ConversationId = EntityId.From("c-other-tenant"), TenantId = OtherTenant, ContactId = Contact,
            Channel = ChannelType.WhatsApp, State = ConversationState.Active, CreatedAt = T0,
        }, CancellationToken.None);

        var all = await ListAsync(world);
        var second = await ListAsync(world, page: 2, pageSize: 2);

        Ids(all).Should().Equal(
            ["c-abandoned", "c-closed", "c-active", "c-tie-b", "c-tie-a"],
            "createdAt descending, then the id descending, terminal states included (design D5 step 3, D9)");
        all.TotalCount.Should().Be(5);
        Ids(second).Should().Equal(["c-active", "c-tie-b"]);
        second.TotalCount.Should().Be(5);
        second.Page.Should().Be(2);
        second.PageSize.Should().Be(2);
    }

    [Fact]
    public async Task ListContactSummariesAsync_ShouldJoinTheQueueTheAgentAndTheDisposition_WhenTheyExist()
    {
        var world = await NewWorldAsync();
        await SeedQueueAsync(world, QueueSupport, "Support");
        await SeedAgentAsync(world, AgentA, "Agent A");
        var typified = await SeedAsync(
            world, "c-typified", ConversationState.Closed, T0, ConversationOwner.ForQueue(QueueSupport), ConversationOwner.ForAgent(AgentA));
        await SeedSubmissionAsync(world, typified, "n-card-replacement", "schema-1", 3);
        // A bot whose id equals the agent's id is not the agent: no display name (design D8).
        await SeedAsync(world, "c-bot", ConversationState.Closed, T0.AddMinutes(-1), new ConversationOwner(ConversationOwnerKind.Bot, AgentA));

        var items = (await ListAsync(world)).Items;

        var summary = items.Single(s => s.Conversation.ConversationId == typified.ConversationId);
        summary.Conversation.QueueId.Should().Be(QueueSupport);
        summary.QueueName.Should().Be("Support");
        summary.Conversation.Owner.Should().Be(ConversationOwner.ForAgent(AgentA));
        summary.AgentDisplayName.Should().Be("Agent A");
        summary.Disposition.Should().Be(new ConversationDispositionRef(
            EntityId.From("n-card-replacement"), EntityId.From("schema-1"), 3));

        var bot = items.Single(s => s.Conversation.ConversationId.Value == "c-bot");
        bot.AgentDisplayName.Should().BeNull();
        bot.QueueName.Should().BeNull("no queue ever owned it");
        bot.Disposition.Should().BeNull("it was never typified");
        bot.LastMessage.Should().BeNull("it has no message");
    }

    [Fact]
    public async Task ListContactSummariesAsync_ShouldTakeTheCorrectedLeaf_WhenASupervisorCorrectedTheDisposition()
    {
        var world = await NewWorldAsync();
        var corrected = await SeedAsync(world, "c-corrected", ConversationState.Closed, T0, ConversationOwner.ForAgent(AgentA));
        await SeedSubmissionAsync(world, corrected, "n-a", "schema-1", 2);
        await world.Corrections.InsertAsync(new TypificationSubmissionCorrection
        {
            TenantId = Tenant,
            ConversationId = corrected.ConversationId,
            CorrectedLeafNodeId = EntityId.From("n-b"),
            CorrectedNodePath = [EntityId.From("n-b")],
            CorrectedByUserId = "supervisor-1",
            CorrectedAt = T0.AddHours(3),
        }, CancellationToken.None);

        var summary = (await ListAsync(world)).Items.Single();

        summary.Disposition.Should().Be(new ConversationDispositionRef(EntityId.From("n-b"), EntityId.From("schema-1"), 2),
            "the correction's leaf wins, read against the submitted schema version (ADR-0034)");
    }

    [Fact]
    public async Task ListContactSummariesAsync_ShouldKeepTheIdsAndOmitTheNames_WhenTheQueueAndTheAgentWereDeleted()
    {
        var world = await NewWorldAsync();
        await SeedQueueAsync(world, QueueSupport, "Support");
        await SeedAgentAsync(world, AgentA, "Agent A");
        await SeedAsync(world, "c-orphan", ConversationState.Closed, T0, ConversationOwner.ForQueue(QueueSupport), ConversationOwner.ForAgent(AgentA));
        await world.Queues.DeleteAsync(Tenant, QueueSupport, CancellationToken.None);
        await world.Agents.DeleteAsync(Tenant, AgentA, CancellationToken.None);

        var summary = (await ListAsync(world)).Items.Single();

        summary.Conversation.QueueId.Should().Be(QueueSupport);
        summary.QueueName.Should().BeNull("the queue no longer exists");
        summary.Conversation.Owner.Should().Be(ConversationOwner.ForAgent(AgentA));
        summary.AgentDisplayName.Should().BeNull("the agent no longer exists");
    }

    [Fact]
    public async Task ListContactSummariesAsync_ShouldTakeTheNewestActivityMessage_WhenASystemNoteAndAFailedSendAreNewer()
    {
        var world = await NewWorldAsync();
        var conversation = await SeedAsync(world, "c-messages", ConversationState.Active, T0, ConversationOwner.ForAgent(AgentA));
        await SeedMessageAsync(world, conversation, "m-1-inbound", MessageDirection.Inbound, T0.AddMinutes(1));
        await SeedMessageAsync(world, conversation, "m-2-outbound", MessageDirection.Outbound, T0.AddMinutes(2),
            new ImageBlock("https://media.example/x.png", null, "image/png"), MessageDeliveryStatus.Sent);
        await SeedMessageAsync(world, conversation, "m-3-failed", MessageDirection.Outbound, T0.AddMinutes(3),
            status: MessageDeliveryStatus.Failed);
        await SeedMessageAsync(world, conversation, "m-4-note", MessageDirection.System, T0.AddMinutes(4));
        var onlyNote = await SeedAsync(world, "c-only-note", ConversationState.Closed, T0.AddMinutes(-5), ConversationOwner.ForAgent(AgentA));
        await SeedMessageAsync(world, onlyNote, "m-only-note", MessageDirection.System, T0.AddMinutes(-4));

        var items = (await ListAsync(world)).Items;

        var last = items.Single(s => s.Conversation.ConversationId == conversation.ConversationId).LastMessage;
        last.Should().NotBeNull();
        last!.Direction.Should().Be(MessageDirection.Outbound, "a System note and a Failed send never count (design D6)");
        last.CreatedAt.Should().Be(T0.AddMinutes(2));
        last.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<ImageBlock>();
        items.Single(s => s.Conversation.ConversationId == onlyNote.ConversationId).LastMessage
            .Should().BeNull("a conversation with only a System note has no activity message");
    }

    [Fact]
    public async Task ListContactSummariesAsync_ShouldBreakTheTieByMessageIdDescending_WhenTwoActivityMessagesShareTheirTime()
    {
        var world = await NewWorldAsync();
        var conversation = await SeedAsync(world, "c-tie", ConversationState.Active, T0, ConversationOwner.ForAgent(AgentA));
        await SeedMessageAsync(world, conversation, "m-b", MessageDirection.Inbound, T0.AddMinutes(1));
        await SeedMessageAsync(world, conversation, "m-a", MessageDirection.Inbound, T0.AddMinutes(1));

        var last = (await ListAsync(world)).Items.Single().LastMessage!;

        last.Content.Blocks.Should().ContainSingle().Which.Should().Be(new TextBlock("m-b"));
    }

    [Fact]
    public async Task ListContactSummariesAsync_ShouldListAndCountOnlyTheAgentsOwnConversations_WhenOwnedByAgentIdIsSet()
    {
        var world = await NewWorldAsync();
        await SeedAsync(world, "c-a-active", ConversationState.Active, T0, ConversationOwner.ForAgent(AgentA));
        await SeedAsync(world, "c-a-closed", ConversationState.Closed, T0.AddMinutes(-1), ConversationOwner.ForAgent(AgentA));
        await SeedAsync(world, "c-b", ConversationState.Closed, T0.AddMinutes(-2), ConversationOwner.ForAgent(AgentB));
        await SeedAsync(world, "c-queue-sharing-id", ConversationState.Queued, T0.AddMinutes(-3), ConversationOwner.ForQueue(AgentA));
        await SeedAsync(world, "c-bot-sharing-id", ConversationState.Closed, T0.AddMinutes(-4), new ConversationOwner(ConversationOwnerKind.Bot, AgentA));
        await SeedAsync(world, "c-offered-to-a", ConversationState.Offered, T0.AddMinutes(-5), ConversationOwner.ForQueue(QueueSupport));

        var restricted = await ListAsync(world, ownedBy: AgentA);
        var unrestricted = await ListAsync(world);

        Ids(restricted).Should().Equal(["c-a-active", "c-a-closed"], "owner kind Agent and id together (design D5 step 3, D18)");
        restricted.TotalCount.Should().Be(2, "the total counts only the caller's own conversations");
        unrestricted.TotalCount.Should().Be(6);
    }
}
