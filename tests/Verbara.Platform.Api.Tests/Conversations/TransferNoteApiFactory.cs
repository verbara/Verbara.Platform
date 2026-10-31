using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// The conversation-ownership host with what a transfer note needs: the host's log records kept, a message store
/// that refuses a chosen conversation's writes, a substitute behind the registry's WhatsApp connector, and helpers that
/// seed and read messages and capture the event bus. WebChat keeps its real connector, so a visitor's socket is real.
/// </summary>
public sealed class TransferNoteApiFactory : ConversationOwnershipApiFactory
{
    private readonly ConcurrentDictionary<EntityId, byte> _refusedWrites = new();

    /// <summary>Every log record of the host.</summary>
    internal LogRecordCapture Logs { get; } = new();

    /// <summary>What the host's channel registry answers for WhatsApp. Shared by the fixture: assert by conversation.</summary>
    public IChannelConnector WhatsAppConnector { get; } = NewWhatsAppConnector();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        return base.CreateHost(builder);
    }

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        var real = services.Last(descriptor => descriptor.ServiceType == typeof(IMessageStore));
        services.Remove(real);
        services.AddSingleton<IMessageStore>(provider =>
            new RefusingMessageStore((IMessageStore)real.ImplementationFactory!(provider), _refusedWrites));

        var registry = services.Last(descriptor => descriptor.ServiceType == typeof(IChannelRegistry));
        services.Remove(registry);
        services.AddSingleton<IChannelRegistry>(provider =>
            new SubstituteWhatsAppRegistry(provider.GetRequiredService<ChannelRegistry>(), WhatsAppConnector));
    }

    private static IChannelConnector NewWhatsAppConnector()
    {
        var connector = Substitute.For<IChannelConnector>();
        connector.Channel.Returns(ChannelType.WhatsApp);
        connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(Success: true, ExternalMessageId: "wamid.substitute", ErrorCode: null, ErrorMessage: null));
        return connector;
    }

    /// <summary>Saves the contact of <paramref name="conversation"/> with one address on the conversation's channel.</summary>
    public void SeedContactOf(Conversation conversation, string address)
    {
        var contact = new Contact
        {
            ContactId = conversation.ContactId,
            TenantId = Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        contact.AddAddress(new ChannelAddress(conversation.Channel, address));
        Services.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Activates WebChat for the customer tenant, which <c>POST /webchat/sessions</c> requires.</summary>
    public void ActivateWebChat() => AuthenticatedPlatformApiFactory.SeedWebChatChannel(Services, Tenant.Value);

    /// <summary>From now on, saving a message of <paramref name="conversationId"/> throws, naming the message's text.</summary>
    public void RefuseWritesTo(EntityId conversationId) => _refusedWrites[conversationId] = 0;

    /// <summary>Stores <paramref name="count"/> inbound messages older than anything a transfer writes now.</summary>
    public void SeedMessages(Conversation conversation, int count)
    {
        var store = Services.GetRequiredService<IMessageStore>();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < count; i++)
        {
            store.SaveAsync(
                new Message
                {
                    MessageId = EntityId.From($"seeded-{i}-{Guid.NewGuid():N}"),
                    ConversationId = conversation.ConversationId,
                    TenantId = Tenant,
                    Direction = MessageDirection.Inbound,
                    Channel = conversation.Channel,
                    Content = new MessageEnvelope([new TextBlock($"seeded message {i}")]),
                    DeliveryStatus = MessageDeliveryStatus.Delivered,
                    CreatedAt = start.AddSeconds(i),
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <summary>Every message the store holds for <paramref name="conversationId"/>, oldest first.</summary>
    public IReadOnlyList<Message> MessagesOf(EntityId conversationId) =>
        Services.GetRequiredService<IMessageStore>()
            .GetConversationMessagesAsync(Tenant, conversationId, int.MaxValue, 0, CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <summary>Keeps every <c>conversation.message</c> event published for <paramref name="conversationId"/> while it lives.</summary>
    public ConversationMessageCapture CaptureMessages(EntityId conversationId) =>
        new(Services.GetRequiredService<PlatformEventBus>(), conversationId.Value);

    /// <summary>Makes publishing a <c>conversation.message</c> event of <paramref name="conversationId"/> throw while it lives.</summary>
    public IDisposable BreakMessagePublishing(EntityId conversationId) =>
        Services.GetRequiredService<PlatformEventBus>().Events.Subscribe(evt =>
        {
            if (evt is ConversationMessageEvent message && message.ConversationId == conversationId.Value)
                throw new InvalidOperationException("a subscriber of the event bus failed");
        });

    private sealed class SubstituteWhatsAppRegistry(IChannelRegistry inner, IChannelConnector whatsApp) : IChannelRegistry
    {
        public IReadOnlyList<ChannelType> AvailableChannels => inner.AvailableChannels;

        public IReadOnlyList<ChannelType> WebhookChannels => inner.WebhookChannels;

        public IChannelConnector GetConnector(ChannelType channel) =>
            channel == ChannelType.WhatsApp ? whatsApp : inner.GetConnector(channel);

        public IWebhookHandler GetHandler(ChannelType channel) => inner.GetHandler(channel);

        public bool TryGetHandler(ChannelType channel, [NotNullWhen(true)] out IWebhookHandler? handler) =>
            inner.TryGetHandler(channel, out handler);

        public ChannelConstraints GetConstraints(ChannelType channel) => inner.GetConstraints(channel);
    }

    private sealed class RefusingMessageStore(IMessageStore inner, ConcurrentDictionary<EntityId, byte> refused) : IMessageStore
    {
        public Task SaveAsync(Message message, CancellationToken ct)
        {
            if (!refused.ContainsKey(message.ConversationId))
                return inner.SaveAsync(message, ct);

            // A database error can quote the row, so the failure names the text a log must not repeat.
            var text = string.Concat(message.Content.Blocks.OfType<TextBlock>().Select(block => block.Text));
            throw new InvalidOperationException($"Failing row contains ({text})");
        }

        public Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct) =>
            inner.GetByIdAsync(tenantId, messageId, ct);

        public Task<IReadOnlyList<Message>> GetConversationMessagesAsync(
            TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct) =>
            inner.GetConversationMessagesAsync(tenantId, conversationId, limit, offset, ct);

        public Task UpdateDeliveryStatusAsync(
            TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct) =>
            inner.UpdateDeliveryStatusAsync(tenantId, messageId, status, timestamp, ct);

        public Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct) =>
            inner.FindByExternalIdAsync(tenantId, externalMessageId, ct);

        public Task MarkSentAsync(TenantId tenantId, EntityId messageId, string? externalMessageId, CancellationToken ct) =>
            inner.MarkSentAsync(tenantId, messageId, externalMessageId, ct);

        public Task<Message> InsertInboundIfAbsentAsync(Message message, CancellationToken ct) =>
            inner.InsertInboundIfAbsentAsync(message, ct);

        public Task<Message?> FindLastInboundAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct) =>
            inner.FindLastInboundAsync(tenantId, conversationId, ct);

        public Task<IReadOnlyList<Message>> GetByConversationIdsAsync(
            TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct) =>
            inner.GetByConversationIdsAsync(tenantId, conversationIds, ct);

        public Task<int> DeleteByConversationIdsAsync(
            TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct) =>
            inner.DeleteByConversationIdsAsync(tenantId, conversationIds, ct);

        public Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct) =>
            inner.DeleteOrphanedAsync(tenantId, ct);
    }
}

/// <summary>The <c>conversation.message</c> events of one conversation, as the event bus published them.</summary>
public sealed class ConversationMessageCapture : IDisposable
{
    private readonly List<ConversationMessageEvent> _events = [];
    private readonly IDisposable _subscription;

    internal ConversationMessageCapture(PlatformEventBus bus, string conversationId) =>
        _subscription = bus.Events.Subscribe(evt =>
        {
            if (evt is ConversationMessageEvent message && message.ConversationId == conversationId)
                lock (_events)
                    _events.Add(message);
        });

    public IReadOnlyList<ConversationMessageEvent> Events
    {
        get
        {
            lock (_events)
                return [.. _events];
        }
    }

    public void Dispose() => _subscription.Dispose();
}
