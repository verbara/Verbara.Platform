using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Core.Pipeline;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// whatsapp-works-for-real — two identical deliveries of a new customer's first message race through the real
/// <see cref="InboundMessagePipeline"/> over the in-memory stores. The interleaving is pinned: both deliveries
/// pass deduplication and find no active conversation before either creates one, so both create a conversation
/// and only one message insert wins. The losing delivery must leave no conversation behind.
/// </summary>
public sealed class InboundMessagePipelineRaceTests
{
    private static readonly TenantId Tenant = new("t-pipeline-race");

    [Fact]
    public async Task ProcessAsync_ShouldLeaveOneConversation_WhenDuplicateDeliveryOfNewCustomerLosesInsertRace()
    {
        using var services = new ServiceCollection().AddInMemoryStorage().BuildServiceProvider();
        var conversationStore = services.GetRequiredService<IConversationStore>();
        var lifecycle = new CreateBarrierLifecycle(
            new DefaultConversationLifecycleService(conversationStore, new SystemClock()), parties: 2);
        var pipeline = new InboundMessagePipeline(
            services.GetRequiredService<IMessageStore>(),
            new FixedContactResolver(Tenant),
            conversationStore,
            lifecycle);
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
        var conversations = (await conversationStore.ListAsync(
            Tenant, new ConversationQuery { PageSize = 100 }, CancellationToken.None)).Items;
        conversations.Should().ContainSingle("the losing delivery's empty conversation must not survive")
            .Which.ConversationId.Should().Be(winner.ConversationId);
    }

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
