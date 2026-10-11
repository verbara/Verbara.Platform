using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Core.Pipeline;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// whatsapp-works-for-real — two identical deliveries of a new customer's first message race through the real
/// <see cref="InboundMessagePipeline"/> on a real Postgres built from the real migrations. Both deliveries pass
/// deduplication and find no active conversation before either creates one, so both create a conversation and
/// the unique provider-id index lets only one message insert win. The losing delivery must leave no conversation
/// behind — otherwise the next customer message would route that empty, owner-less queued conversation.
/// </summary>
public sealed class PostgresInboundPipelineRaceTests : IClassFixture<MessageCorrelationFixture>
{
    private static readonly TenantId Tenant = new("t-pipeline-race");

    private readonly MessageCorrelationFixture _fixture;

    public PostgresInboundPipelineRaceTests(MessageCorrelationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProcessAsync_ShouldLeaveOneConversation_WhenDuplicateDeliveryOfNewCustomerLosesInsertRace()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var conversationStore = new PostgresConversationStore(ds);
        var lifecycle = new CreateBarrierLifecycle(
            new DefaultConversationLifecycleService(conversationStore, new SystemClock()), parties: 2);
        var pipeline = new InboundMessagePipeline(
            new PostgresMessageStore(ds), new FixedContactResolver(Tenant), conversationStore, lifecycle);
        var inbound = new InboundMessage(
            From: new ChannelAddress(ChannelType.WhatsApp, "+573001112233"),
            Content: new MessageEnvelope([new TextBlock("hola")]),
            ExternalMessageId: "wamid.pipeline-race",
            Timestamp: DateTimeOffset.UtcNow);

        var results = await Task.WhenAll(
            Task.Run(() => pipeline.ProcessAsync(inbound, Tenant, ChannelType.WhatsApp, CancellationToken.None)),
            Task.Run(() => pipeline.ProcessAsync(inbound, Tenant, ChannelType.WhatsApp, CancellationToken.None)));

        lifecycle.Created.Should().Be(2, "the pinned interleaving makes both deliveries create a conversation");
        var winner = results.Should().ContainSingle(r => !r.IsDuplicate).Subject;
        results.Should().ContainSingle(r => r.IsDuplicate);
        (await MessageCorrelationFixture.ScalarAsync(ds, "SELECT COUNT(*) FROM messages")).Should().Be(1);
        (await MessageCorrelationFixture.ScalarAsync(ds, "SELECT COUNT(*) FROM conversations"))
            .Should().Be(1, "the losing delivery's empty conversation must not survive");
        (await conversationStore.GetByIdAsync(Tenant, winner.ConversationId, CancellationToken.None))
            .Should().NotBeNull("the surviving conversation is the one holding the stored message");
    }

    [Fact]
    public async Task DeleteIfNoMessagesAsync_ShouldDeleteOnlyEmptyConversation_WhenTenantMatches()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var conversations = new PostgresConversationStore(ds);
        var messages = new PostgresMessageStore(ds);
        var empty = await NewConversationAsync(conversations, Tenant);
        var withMessage = await NewConversationAsync(conversations, Tenant);
        var otherTenant = await NewConversationAsync(conversations, new TenantId("t-other"));
        await messages.InsertInboundIfAbsentAsync(new Message
        {
            MessageId = EntityId.New(),
            ConversationId = withMessage,
            TenantId = Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([new TextBlock("hola")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = "wamid.kept",
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        (await conversations.DeleteIfNoMessagesAsync(Tenant, empty, CancellationToken.None)).Should().BeTrue();
        (await conversations.DeleteIfNoMessagesAsync(Tenant, withMessage, CancellationToken.None))
            .Should().BeFalse("a conversation holding a message is never discarded");
        (await conversations.DeleteIfNoMessagesAsync(Tenant, otherTenant, CancellationToken.None))
            .Should().BeFalse("the delete is tenant-scoped");

        (await conversations.GetByIdAsync(Tenant, empty, CancellationToken.None)).Should().BeNull();
        (await conversations.GetByIdAsync(Tenant, withMessage, CancellationToken.None)).Should().NotBeNull();
        (await conversations.GetByIdAsync(new TenantId("t-other"), otherTenant, CancellationToken.None)).Should().NotBeNull();
    }

    private static async Task<EntityId> NewConversationAsync(PostgresConversationStore store, TenantId tenant) =>
        (await new DefaultConversationLifecycleService(store, new SystemClock())
            .CreateAsync(tenant, EntityId.New(), ChannelType.WhatsApp, CancellationToken.None)).ConversationId;

    /// <summary>Resolves every address to one contact.</summary>
    private sealed class FixedContactResolver(TenantId tenant) : IContactIdentityResolver
    {
        private readonly Contact _contact = new()
        {
            ContactId = EntityId.New(),
            TenantId = tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        public Task<Contact> ResolveAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct) =>
            Task.FromResult(_contact);
    }

    /// <summary>
    /// Holds every <see cref="CreateAsync"/> until <c>parties</c> callers have reached it, so each caller has
    /// already deduplicated and looked for an active conversation before any conversation exists.
    /// </summary>
    private sealed class CreateBarrierLifecycle(IConversationLifecycleService inner, int parties) : IConversationLifecycleService
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _created;

        public int Created => Volatile.Read(ref _created);

        public async Task<Conversation> CreateAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) == parties)
                _allArrived.SetResult();
            await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var conversation = await inner.CreateAsync(tenantId, contactId, channel, ct);
            Interlocked.Increment(ref _created);
            return conversation;
        }

        public Task TransitionAsync(TenantId tenantId, EntityId conversationId, ConversationState newState, CancellationToken ct) =>
            inner.TransitionAsync(tenantId, conversationId, newState, ct);

        public Task CloseAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct) =>
            inner.CloseAsync(tenantId, conversationId, ct);
    }
}
