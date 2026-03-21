using Asterisk.Platform.Channels.Core.Pipeline;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Services;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using NSubstitute;

namespace Asterisk.Platform.Channels.Core.Tests.Pipeline;

public class InboundMessagePipelineTests
{
    private static readonly TenantId TenantId = new("tenant-1");
    private static readonly ChannelType Channel = ChannelType.WhatsApp;

    private readonly IMessageStore _messageStore;
    private readonly IContactIdentityResolver _contactResolver;
    private readonly IConversationStore _conversationStore;
    private readonly IConversationLifecycleService _lifecycleService;
    private readonly InboundMessagePipeline _pipeline;

    public InboundMessagePipelineTests()
    {
        _messageStore = Substitute.For<IMessageStore>();
        _contactResolver = Substitute.For<IContactIdentityResolver>();
        _conversationStore = Substitute.For<IConversationStore>();
        _lifecycleService = Substitute.For<IConversationLifecycleService>();

        _pipeline = new InboundMessagePipeline(
            _messageStore,
            _contactResolver,
            _conversationStore,
            _lifecycleService);
    }

    private static InboundMessage MakeMessage(string externalId = "ext-1") =>
        new(
            From: new ChannelAddress(Channel, "+15551234567"),
            Content: new MessageEnvelope([]),
            ExternalMessageId: externalId,
            Timestamp: DateTimeOffset.UtcNow);

    private static Contact MakeContact(string? id = null) =>
        new()
        {
            ContactId = id is not null ? EntityId.From(id) : EntityId.New(),
            TenantId = TenantId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static Conversation MakeConversation(EntityId contactId, bool isNew = false) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = TenantId,
            ContactId = contactId,
            Channel = Channel,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    // -------------------------------------------------------------------------
    // Full pipeline: new contact + new conversation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_ShouldCreateContactAndConversation_WhenNewMessage()
    {
        var inbound = MakeMessage();
        var contact = MakeContact();
        var conversation = MakeConversation(contact.ContactId, isNew: true);

        _messageStore.FindByExternalIdAsync(TenantId, inbound.ExternalMessageId, Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _contactResolver.ResolveAsync(TenantId, inbound.From, Arg.Any<CancellationToken>())
            .Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        _lifecycleService.CreateAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(conversation);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.ContactId.Should().Be(contact.ContactId);
        result.ConversationId.Should().Be(conversation.ConversationId);
        result.IsNewConversation.Should().BeTrue();
    }

    [Fact]
    public async Task Pipeline_ShouldReuseExistingContact_WhenKnownAddress()
    {
        var inbound = MakeMessage();
        var contact = MakeContact("known-contact");
        var conversation = MakeConversation(contact.ContactId);

        _messageStore.FindByExternalIdAsync(default, default!, default)
            .ReturnsForAnyArgs((Message?)null);
        _contactResolver.ResolveAsync(TenantId, inbound.From, Arg.Any<CancellationToken>())
            .Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(conversation);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.ContactId.Should().Be(contact.ContactId);
        await _lifecycleService.DidNotReceive().CreateAsync(default, default, default, default);
    }

    [Fact]
    public async Task Pipeline_ShouldReuseExistingConversation_WhenActive()
    {
        var inbound = MakeMessage();
        var contact = MakeContact();
        var existingConversation = MakeConversation(contact.ContactId);

        _messageStore.FindByExternalIdAsync(TenantId, inbound.ExternalMessageId, Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _contactResolver.ResolveAsync(TenantId, inbound.From, Arg.Any<CancellationToken>())
            .Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(existingConversation);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.ConversationId.Should().Be(existingConversation.ConversationId);
        result.IsNewConversation.Should().BeFalse();
        await _lifecycleService.DidNotReceive().CreateAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pipeline_ShouldCreateNewConversation_WhenNoneActive()
    {
        var inbound = MakeMessage();
        var contact = MakeContact();
        var newConversation = MakeConversation(contact.ContactId, isNew: true);

        _messageStore.FindByExternalIdAsync(TenantId, inbound.ExternalMessageId, Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _contactResolver.ResolveAsync(TenantId, inbound.From, Arg.Any<CancellationToken>())
            .Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        _lifecycleService.CreateAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(newConversation);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.ConversationId.Should().Be(newConversation.ConversationId);
        result.IsNewConversation.Should().BeTrue();
        await _lifecycleService.Received(1).CreateAsync(
            TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Deduplication short-circuit
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_ShouldShortCircuit_WhenDuplicateMessage()
    {
        var inbound = MakeMessage("dup-ext-1");
        var contact = MakeContact();
        var conversationId = EntityId.New();
        var existingMessage = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversationId,
            TenantId = TenantId,
            Direction = MessageDirection.Inbound,
            Channel = Channel,
            Content = inbound.Content,
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = "dup-ext-1",
            CreatedAt = inbound.Timestamp,
        };

        _messageStore.FindByExternalIdAsync(TenantId, "dup-ext-1", Arg.Any<CancellationToken>())
            .Returns(existingMessage);
        _contactResolver.ResolveAsync(TenantId, inbound.From, Arg.Any<CancellationToken>())
            .Returns(contact);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.MessageId.Should().Be(existingMessage.MessageId);
        result.ConversationId.Should().Be(conversationId);
        result.IsNewConversation.Should().BeFalse();

        // Conversation store and lifecycle must not be called
        await _conversationStore.DidNotReceive().FindActiveByContactAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
        await _lifecycleService.DidNotReceive().CreateAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
        // SaveAsync must not be called again
        await _messageStore.DidNotReceive().SaveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Message persistence
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_ShouldPersistMessage_WithCorrectDirection()
    {
        var inbound = MakeMessage();
        var contact = MakeContact();
        var conversation = MakeConversation(contact.ContactId);

        _messageStore.FindByExternalIdAsync(TenantId, inbound.ExternalMessageId, Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _contactResolver.ResolveAsync(TenantId, inbound.From, Arg.Any<CancellationToken>())
            .Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(conversation);

        await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        await _messageStore.Received(1).SaveAsync(
            Arg.Is<Message>(m =>
                m.Direction == MessageDirection.Inbound &&
                m.DeliveryStatus == MessageDeliveryStatus.Delivered &&
                m.Channel == Channel &&
                m.TenantId == TenantId &&
                m.ExternalMessageId == inbound.ExternalMessageId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pipeline_ShouldSetIsNewConversation_WhenCreated()
    {
        var inbound = MakeMessage();
        var contact = MakeContact();
        var newConversation = MakeConversation(contact.ContactId);

        _messageStore.FindByExternalIdAsync(TenantId, inbound.ExternalMessageId, Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _contactResolver.ResolveAsync(TenantId, inbound.From, Arg.Any<CancellationToken>())
            .Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        _lifecycleService.CreateAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(newConversation);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.IsNewConversation.Should().BeTrue();
    }

    [Fact]
    public async Task Pipeline_ShouldReturnCorrectMessageId_AfterPersistence()
    {
        var inbound = MakeMessage();
        var contact = MakeContact();
        var conversation = MakeConversation(contact.ContactId);

        _messageStore.FindByExternalIdAsync(TenantId, inbound.ExternalMessageId, Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _contactResolver.ResolveAsync(TenantId, inbound.From, Arg.Any<CancellationToken>())
            .Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(conversation);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.MessageId.Value.Should().NotBeNullOrWhiteSpace();
    }

    // -------------------------------------------------------------------------
    // Step unit tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeduplicateStep_ShouldSetIsDuplicate_WhenMessageAlreadyExists()
    {
        var inbound = MakeMessage("existing-ext");
        var existing = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = EntityId.New(),
            TenantId = TenantId,
            Direction = MessageDirection.Inbound,
            Channel = Channel,
            Content = inbound.Content,
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = "existing-ext",
            CreatedAt = inbound.Timestamp,
        };

        _messageStore.FindByExternalIdAsync(TenantId, "existing-ext", Arg.Any<CancellationToken>())
            .Returns(existing);

        var context = new PipelineContext
        {
            InboundMessage = inbound,
            TenantId = TenantId,
            Channel = Channel,
        };

        var step = new DeduplicateStep(_messageStore);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsDuplicate.Should().BeTrue();
        result.PersistedMessage.Should().BeSameAs(existing);
    }

    [Fact]
    public async Task DeduplicateStep_ShouldNotSetIsDuplicate_WhenMessageIsNew()
    {
        var inbound = MakeMessage("new-ext");

        _messageStore.FindByExternalIdAsync(TenantId, "new-ext", Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var context = new PipelineContext
        {
            InboundMessage = inbound,
            TenantId = TenantId,
            Channel = Channel,
        };

        var step = new DeduplicateStep(_messageStore);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsDuplicate.Should().BeFalse();
        result.PersistedMessage.Should().BeNull();
    }

    [Fact]
    public async Task ContactResolutionStep_ShouldSetContact_AfterResolution()
    {
        var inbound = MakeMessage();
        var contact = MakeContact("resolved-id");

        _contactResolver.ResolveAsync(TenantId, inbound.From, Arg.Any<CancellationToken>())
            .Returns(contact);

        var context = new PipelineContext
        {
            InboundMessage = inbound,
            TenantId = TenantId,
            Channel = Channel,
        };

        var step = new ContactResolutionStep(_contactResolver);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Contact.Should().BeSameAs(contact);
        result.Contact!.ContactId.Should().Be(EntityId.From("resolved-id"));
    }

    [Fact]
    public async Task ConversationResolutionStep_ShouldMarkIsNewConversation_WhenCreated()
    {
        var inbound = MakeMessage();
        var contact = MakeContact();
        var newConversation = MakeConversation(contact.ContactId);

        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        _lifecycleService.CreateAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(newConversation);

        var context = new PipelineContext
        {
            InboundMessage = inbound,
            TenantId = TenantId,
            Channel = Channel,
            Contact = contact,
        };

        var step = new ConversationResolutionStep(_conversationStore, _lifecycleService);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsNewConversation.Should().BeTrue();
        result.Conversation.Should().BeSameAs(newConversation);
    }

    [Fact]
    public async Task ConversationResolutionStep_ShouldNotMarkIsNew_WhenExistingConversation()
    {
        var inbound = MakeMessage();
        var contact = MakeContact();
        var existing = MakeConversation(contact.ContactId);

        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(existing);

        var context = new PipelineContext
        {
            InboundMessage = inbound,
            TenantId = TenantId,
            Channel = Channel,
            Contact = contact,
        };

        var step = new ConversationResolutionStep(_conversationStore, _lifecycleService);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsNewConversation.Should().BeFalse();
        result.Conversation.Should().BeSameAs(existing);
        await _lifecycleService.DidNotReceive().CreateAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MessagePersistenceStep_ShouldPersistMessage_WithInboundDirection()
    {
        var inbound = MakeMessage();
        var contact = MakeContact();
        var conversation = MakeConversation(contact.ContactId);

        var context = new PipelineContext
        {
            InboundMessage = inbound,
            TenantId = TenantId,
            Channel = Channel,
            Contact = contact,
            Conversation = conversation,
        };

        var step = new MessagePersistenceStep(_messageStore);
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.PersistedMessage.Should().NotBeNull();
        result.PersistedMessage!.Direction.Should().Be(MessageDirection.Inbound);
        result.PersistedMessage.DeliveryStatus.Should().Be(MessageDeliveryStatus.Delivered);
        result.PersistedMessage.ConversationId.Should().Be(conversation.ConversationId);
        result.PersistedMessage.TenantId.Should().Be(TenantId);
        result.PersistedMessage.ExternalMessageId.Should().Be(inbound.ExternalMessageId);

        await _messageStore.Received(1).SaveAsync(result.PersistedMessage, Arg.Any<CancellationToken>());
    }
}
