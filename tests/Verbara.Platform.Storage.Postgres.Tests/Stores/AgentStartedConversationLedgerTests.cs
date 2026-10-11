using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// agent-started-conversations (whatsapp-works-for-real design D14) against a real Postgres. An agent starting a
/// conversation in its own name does not change the set of counted agents and is not an ownership move, so the
/// start writes no <c>license_agent_events</c> row: the immutable <c>lac1</c> chain keeps its length and its head.
/// The conversation is persisted through the conversation store, born <see cref="ConversationState.Active"/> and
/// owned, and its first message through the message store. The starter is resolved from DI with metering's real
/// writer registered beside it, so a start that went through the writer would show up in the chain.
/// </summary>
public sealed class AgentStartedConversationLedgerTests : IClassFixture<AgentIdentityFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId T1 = new("t1");
    private static readonly EntityId Contact = EntityId.From("contact-wa-1");
    private readonly AgentIdentityFixture _fixture;

    public AgentStartedConversationLedgerTests(AgentIdentityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StartAsync_ShouldLeaveLicenseLedgerUnchanged_WhenAgentStartsInOwnName()
    {
        var h = await PreparedAsync();
        await SaveContactAsync(h);
        var before = await h.EventsAsync("t1");

        var outcome = await StarterOver(h).StartAsync(Request(), CancellationToken.None);

        outcome.IsRefused.Should().BeFalse(outcome.RefusalCode);
        var after = await h.EventsAsync("t1");
        after.Count.Should().Be(before.Count, "an own-name start appends no licensed-agent ledger row");
        after[^1].RowHash.Should().Be(before[^1].RowHash, "the lac1 chain head is unchanged");
        (await h.CountAsync("SELECT COUNT(*) FROM license_agent_events")).Should().Be(before.Count);
    }

    [Fact]
    public async Task StartAsync_ShouldPersistBornActiveOwnedConversationAndSentMessage_WhenSendSucceeds()
    {
        var h = await PreparedAsync();
        await SaveContactAsync(h);

        var outcome = await StarterOver(h).StartAsync(Request(), CancellationToken.None);

        var found = await new PostgresConversationStore(h.Ds)
            .FindActiveByContactAsync(T1, Contact, ChannelType.WhatsApp, CancellationToken.None);
        found.Should().NotBeNull();
        found!.ConversationId.Should().Be(outcome.Conversation!.ConversationId);
        found.State.Should().Be(ConversationState.Active);
        found.Owner.Should().Be(ConversationOwner.ForAgent(EntityId.From("a1")));
        found.CreatedBy.Should().Be("u-a1");
        var message = (await new PostgresMessageStore(h.Ds)
            .GetConversationMessagesAsync(T1, found.ConversationId, 10, 0, CancellationToken.None)).Should().ContainSingle().Subject;
        message.DeliveryStatus.Should().Be(MessageDeliveryStatus.Sent);
        message.ExternalMessageId.Should().Be("wamid.pg-start");
    }

    [Fact]
    public async Task FindActiveByContactAsync_ShouldReturnTheOtherAgentsConversation_WhenStartedByAnotherAgent()
    {
        var h = await PreparedAsync();
        await h.CreateAgentAsync("t1", "a2");
        await SaveContactAsync(h);
        await StarterOver(h).StartAsync(Request(), CancellationToken.None);

        var found = await new PostgresConversationStore(h.Ds)
            .FindActiveByContactAsync(T1, Contact, ChannelType.WhatsApp, CancellationToken.None);

        // What a second agent's start reads to refuse with conversation-open-elsewhere (owner kind Agent, not a2).
        found!.Owner!.Kind.Should().Be(ConversationOwnerKind.Agent);
        found.Owner.OwnerId.Should().Be(EntityId.From("a1"));
        found.Owner.OwnerId.Should().NotBe(EntityId.From("a2"));
    }

    [Fact]
    public async Task FindLastInboundAtAsync_ShouldReturnNewestInboundAcrossContactConversations_WhenSeveralExist()
    {
        var h = await PreparedAsync();
        var store = new PostgresConversationStore(h.Ds);
        var messages = new PostgresMessageStore(h.Ds);
        var closed = await SaveWindowConversationAsync(store, Contact, ChannelType.WhatsApp, ConversationState.Closed);
        var active = await SaveWindowConversationAsync(store, Contact, ChannelType.WhatsApp, ConversationState.Active);
        var webChat = await SaveWindowConversationAsync(store, Contact, ChannelType.WebChat, ConversationState.Active);
        var otherContact = await SaveWindowConversationAsync(store, EntityId.From("contact-other"), ChannelType.WhatsApp, ConversationState.Active);
        await SaveWindowMessageAsync(messages, closed, MessageDirection.Inbound, T0.AddHours(-2));
        await SaveWindowMessageAsync(messages, active, MessageDirection.Inbound, T0.AddHours(-5));
        await SaveWindowMessageAsync(messages, active, MessageDirection.Outbound, T0.AddMinutes(-30));
        await SaveWindowMessageAsync(messages, webChat, MessageDirection.Inbound, T0.AddMinutes(-10));
        await SaveWindowMessageAsync(messages, otherContact, MessageDirection.Inbound, T0.AddMinutes(-5));

        var last = await store.FindLastInboundAtAsync(T1, Contact, ChannelType.WhatsApp, CancellationToken.None);

        last.Should().Be(T0.AddHours(-2), "the newest inbound WhatsApp message of this contact, in any conversation");
        (await store.FindLastInboundAtAsync(new TenantId("t2"), Contact, ChannelType.WhatsApp, CancellationToken.None))
            .Should().BeNull();
        (await store.FindLastInboundAtAsync(T1, EntityId.From("contact-silent"), ChannelType.WhatsApp, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_ShouldSendFreeFormWithoutTemplate_WhenContactWroteWithin24hInAnotherConversation()
    {
        var h = await PreparedAsync();
        await SaveContactAsync(h);
        var store = new PostgresConversationStore(h.Ds);
        var earlier = await SaveWindowConversationAsync(store, Contact, ChannelType.WhatsApp, ConversationState.Closed);
        await SaveWindowMessageAsync(new PostgresMessageStore(h.Ds), earlier, MessageDirection.Inbound, T0.AddHours(-3));

        var outcome = await StarterOver(h).StartAsync(Request() with { TemplateName = null, TemplateLanguage = null }, CancellationToken.None);

        outcome.IsRefused.Should().BeFalse(outcome.RefusalCode);
        outcome.Conversation!.ConversationId.Should().NotBe(earlier.ConversationId);
    }

    private static async Task<Conversation> SaveWindowConversationAsync(
        PostgresConversationStore store, EntityId contact, ChannelType channel, ConversationState state)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = T1,
            ContactId = contact,
            Channel = channel,
            State = state,
            CreatedAt = T0.AddDays(-1),
        };
        await store.SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    private static Task SaveWindowMessageAsync(
        PostgresMessageStore messages, Conversation conversation, MessageDirection direction, DateTimeOffset at) =>
        messages.SaveAsync(new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversation.ConversationId,
            TenantId = conversation.TenantId,
            Direction = direction,
            Channel = conversation.Channel,
            Content = new MessageEnvelope([new TextBlock("hi")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = at,
        }, CancellationToken.None);

    private async Task<LicenseLedgerHarness> PreparedAsync()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);
        await h.TenantAsync("t1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("t1", "a1");
        return h;
    }

    private static Task SaveContactAsync(LicenseLedgerHarness h)
    {
        var contact = new Contact { ContactId = Contact, TenantId = T1, CreatedAt = T0 };
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "+15550001111"));
        return new PostgresContactStore(h.Ds).SaveAsync(contact, CancellationToken.None);
    }

    private static OutboundStartRequest Request() =>
        new(T1, Contact, ChannelType.WhatsApp, EntityId.From("a1"), "u-a1",
            new MessageEnvelope([new TextBlock("Hello from your agent")]), "hello_world", "en_US");

    // The production composition (AddSwitchboard) over the real Postgres stores and metering's real writer.
    private static IOutboundConversationStarter StarterOver(LicenseLedgerHarness h)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IClock>(h.Clock);
        services.AddSingleton<PlatformEventBus>();
        services.AddSingleton<IConversationStore>(new PostgresConversationStore(h.Ds));
        services.AddSingleton<IMessageStore>(new PostgresMessageStore(h.Ds));
        services.AddSingleton<IContactStore>(new PostgresContactStore(h.Ds));
        services.AddSingleton<ILicensedAgentOwnershipWriter>(h.Writer);
        services.AddSingleton<IAgentCapacityService>(new InMemoryAgentCapacityService(new DefaultCapacity()));
        services.AddSingleton<IChannelRegistry>(new ChannelRegistry([], [new AcceptingConnector()]));
        services.AddSwitchboard();
        return services.BuildServiceProvider().GetRequiredService<IOutboundConversationStarter>();
    }

    private sealed class DefaultCapacity : IAgentCapacityResolver
    {
        public Task<ChannelCapacity?> ResolveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) =>
            Task.FromResult<ChannelCapacity?>(new ChannelCapacity());
    }

    private sealed class AcceptingConnector : IChannelConnector
    {
        public ChannelType Channel => ChannelType.WhatsApp;

        public Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct) =>
            Task.FromResult(new SendResult(true, "wamid.pg-start", null, null));

        public Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct) =>
            Task.FromResult<MessageDeliveryStatus?>(null);
    }
}
