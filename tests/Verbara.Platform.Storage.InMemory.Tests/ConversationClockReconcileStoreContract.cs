using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// conversations-close-after-inactivity task 2.8 (design D2, "Clock reconciliation on every start"): the store half of
/// the reconcile, run against every store. <c>ReconcileLastMessageAtAsync</c> advances a stale clock to the newest
/// activity message (an <c>Inbound</c> message, or an <c>Outbound</c> one not stored <c>Failed</c>), ignores
/// <c>System</c> and <c>Failed</c> <c>Outbound</c> messages, never moves a clock back, leaves terminal, voice and other
/// tenants' rows untouched, and is idempotent. The messages are stored through the message store as an older binary
/// would: without touching the conversation row.
/// </summary>
/// <remarks>Linked into Verbara.Platform.Storage.Postgres.Tests.</remarks>
public abstract class ConversationClockReconcileStoreContract
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("t-reconcile");
    private static readonly TenantId OtherTenant = new("t-reconcile-other");

    /// <summary>A new, empty conversation store and the message store it reconciles from.</summary>
    protected abstract Task<(IConversationStore Conversations, IMessageStore Messages)> NewStoresAsync();

    private static async Task<Conversation> SeedAsync(
        IConversationStore store, ConversationState state, DateTimeOffset clock, ChannelType channel = ChannelType.WhatsApp,
        TenantId? tenant = null)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"c-{Guid.NewGuid():N}"),
            TenantId = tenant ?? Tenant,
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = channel,
            State = state,
            Owner = ConversationOwner.ForAgent(EntityId.From("agent-a")),
            CreatedAt = clock.AddHours(-1),
            LastMessageAt = clock,
        };
        await store.SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    private static Task StoreMessageAsync(
        IMessageStore messages, Conversation conversation, MessageDirection direction, MessageDeliveryStatus status,
        DateTimeOffset createdAt) =>
        messages.SaveAsync(new Message
        {
            MessageId = EntityId.From($"m-{Guid.NewGuid():N}"),
            ConversationId = conversation.ConversationId,
            TenantId = conversation.TenantId,
            Direction = direction,
            Channel = conversation.Channel,
            Content = new MessageEnvelope([new TextBlock("hola")]),
            DeliveryStatus = status,
            CreatedAt = createdAt,
        }, CancellationToken.None);

    private static async Task<DateTimeOffset?> ClockOfAsync(IConversationStore store, Conversation conversation) =>
        (await store.GetByIdAsync(conversation.TenantId, conversation.ConversationId, CancellationToken.None))!.LastMessageAt;

    [Fact]
    public async Task ReconcileLastMessageAtAsync_ShouldAdvanceAStaleClockToTheNewestActivityMessage_WhenInboundOrNotFailedOutbound()
    {
        var (store, messages) = await NewStoresAsync();
        var inbound = await SeedAsync(store, ConversationState.Active, Now.AddHours(-80));
        await StoreMessageAsync(messages, inbound, MessageDirection.Inbound, MessageDeliveryStatus.Delivered, Now.AddHours(-30));
        await StoreMessageAsync(messages, inbound, MessageDirection.Inbound, MessageDeliveryStatus.Delivered, Now.AddHours(-1));
        var outbound = await SeedAsync(store, ConversationState.WaitingForCustomer, Now.AddHours(-80));
        await StoreMessageAsync(messages, outbound, MessageDirection.Outbound, MessageDeliveryStatus.Sent, Now.AddHours(-2));
        var otherTenant = await SeedAsync(store, ConversationState.Active, Now.AddHours(-80), tenant: OtherTenant);
        await StoreMessageAsync(messages, otherTenant, MessageDirection.Inbound, MessageDeliveryStatus.Delivered, Now.AddHours(-1));

        var advanced = await store.ReconcileLastMessageAtAsync(Tenant, CancellationToken.None);

        advanced.Should().Be(2);
        (await ClockOfAsync(store, inbound)).Should().Be(Now.AddHours(-1), "the newest inbound message is the clock");
        (await ClockOfAsync(store, outbound)).Should().Be(Now.AddHours(-2), "an Outbound message not stored Failed counts");
        (await ClockOfAsync(store, otherTenant)).Should().Be(Now.AddHours(-80), "the reconcile is scoped to one tenant");
    }

    [Fact]
    public async Task ReconcileLastMessageAtAsync_ShouldIgnoreSystemAndFailedOutboundMessages_WhenTheyAreNewerThanTheClock()
    {
        var (store, messages) = await NewStoresAsync();
        var conversation = await SeedAsync(store, ConversationState.Active, Now.AddHours(-80));
        await StoreMessageAsync(messages, conversation, MessageDirection.System, MessageDeliveryStatus.Delivered, Now.AddHours(-1));
        await StoreMessageAsync(messages, conversation, MessageDirection.Outbound, MessageDeliveryStatus.Failed, Now.AddHours(-1));

        var advanced = await store.ReconcileLastMessageAtAsync(Tenant, CancellationToken.None);

        advanced.Should().Be(0);
        (await ClockOfAsync(store, conversation)).Should().Be(Now.AddHours(-80),
            "a System message and an Outbound message stored Failed are not activity (D2)");
    }

    [Fact]
    public async Task ReconcileLastMessageAtAsync_ShouldNeverMoveTheClockBack_WhenTheClockIsNewerThanEveryMessage()
    {
        var (store, messages) = await NewStoresAsync();
        var conversation = await SeedAsync(store, ConversationState.Active, Now.AddHours(-1));
        await StoreMessageAsync(messages, conversation, MessageDirection.Inbound, MessageDeliveryStatus.Delivered, Now.AddHours(-10));

        var advanced = await store.ReconcileLastMessageAtAsync(Tenant, CancellationToken.None);

        advanced.Should().Be(0);
        (await ClockOfAsync(store, conversation)).Should().Be(Now.AddHours(-1));
    }

    [Theory]
    [InlineData(ConversationState.Closed, ChannelType.WhatsApp)]
    [InlineData(ConversationState.Abandoned, ChannelType.WebChat)]
    [InlineData(ConversationState.Active, ChannelType.Voice)]
    public async Task ReconcileLastMessageAtAsync_ShouldLeaveTheRowUntouched_WhenItIsTerminalOrVoice(
        ConversationState state, ChannelType channel)
    {
        var (store, messages) = await NewStoresAsync();
        var conversation = await SeedAsync(store, state, Now.AddHours(-80), channel);
        await StoreMessageAsync(messages, conversation, MessageDirection.Inbound, MessageDeliveryStatus.Delivered, Now.AddHours(-1));

        var advanced = await store.ReconcileLastMessageAtAsync(Tenant, CancellationToken.None);

        advanced.Should().Be(0);
        (await ClockOfAsync(store, conversation)).Should().Be(Now.AddHours(-80));
    }

    [Fact]
    public async Task ReconcileLastMessageAtAsync_ShouldChangeNothing_WhenRunASecondTime()
    {
        var (store, messages) = await NewStoresAsync();
        var conversation = await SeedAsync(store, ConversationState.OnHold, Now.AddHours(-80));
        await StoreMessageAsync(messages, conversation, MessageDirection.Inbound, MessageDeliveryStatus.Delivered, Now.AddHours(-3));

        var first = await store.ReconcileLastMessageAtAsync(Tenant, CancellationToken.None);
        var second = await store.ReconcileLastMessageAtAsync(Tenant, CancellationToken.None);

        first.Should().Be(1);
        second.Should().Be(0, "the reconcile is idempotent: a second run advances no row");
        (await ClockOfAsync(store, conversation)).Should().Be(Now.AddHours(-3));
    }
}
