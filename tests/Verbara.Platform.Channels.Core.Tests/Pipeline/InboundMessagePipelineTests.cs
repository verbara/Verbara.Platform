using Verbara.Platform.Channels.Core.Pipeline;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace Verbara.Platform.Channels.Core.Tests.Pipeline;

public class InboundMessagePipelineTests
{
    private static readonly TenantId TenantId = new("tenant-1");
    private static readonly ChannelType Channel = ChannelType.WhatsApp;

    private readonly IMessageStore _messageStore;
    private readonly IContactIdentityResolver _contactResolver;
    private readonly IConversationStore _conversationStore;
    private readonly IConversationLifecycleService _lifecycleService;
    private readonly IClock _clock;
    private readonly InboundMessagePipeline _pipeline;

    public InboundMessagePipelineTests()
    {
        _messageStore = Substitute.For<IMessageStore>();
        _contactResolver = Substitute.For<IContactIdentityResolver>();
        _conversationStore = Substitute.For<IConversationStore>();
        _lifecycleService = Substitute.For<IConversationLifecycleService>();
        _messageStore.InsertInboundIfAbsentAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Message>());
        // conversations-close-after-inactivity D7: the inbound claim on an open conversation wins unless a test says so.
        _conversationStore.TryClaimForInboundAsync(
                Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _clock = Substitute.For<IClock>();
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        _pipeline = new InboundMessagePipeline(
            _messageStore,
            _contactResolver,
            _conversationStore,
            _lifecycleService,
            _clock);
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
    // A WebChat session message that opens a conversation records its session address (design D9)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_ShouldOpenConversationForTheSessionAddress_WhenWebChatMessageFindsNoConversation()
    {
        var sessionAddress = new ChannelAddress(ChannelType.WebChat, "webchat-7c9e6679f2a54d0b8e1a3b5c7d9f1e2a");
        var inbound = new InboundMessage(sessionAddress, new MessageEnvelope([]), "ext-webchat", DateTimeOffset.UtcNow);
        var contact = MakeContact();
        var opened = MakeConversation(contact.ContactId);
        _contactResolver.ResolveAsync(TenantId, Arg.Any<ContactIdentity>(), Arg.Any<CancellationToken>()).Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        _lifecycleService.CreateForWebChatSessionAsync(TenantId, contact.ContactId, sessionAddress, Arg.Any<CancellationToken>())
            .Returns(opened);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, ChannelType.WebChat, CancellationToken.None);

        result.ConversationId.Should().Be(opened.ConversationId);
        result.IsNewConversation.Should().BeTrue();
        await _lifecycleService.DidNotReceiveWithAnyArgs().CreateAsync(default, default, default, default);
    }

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
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>())
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
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>())
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
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>())
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
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>())
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
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>())
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
        // Nothing must be persisted again
        await _messageStore.DidNotReceive().SaveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _messageStore.DidNotReceive().InsertInboundIfAbsentAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
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
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>())
            .Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(conversation);

        await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        await _messageStore.Received(1).InsertInboundIfAbsentAsync(
            Arg.Is<Message>(m => m != null &&
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
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>())
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
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>())
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

        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>())
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
    public async Task ContactResolutionStep_ShouldPassAlternateAddressesAndProfileName_WhenInboundCarriesThem()
    {
        var phone = new ChannelAddress(ChannelType.WhatsApp, "15550000002");
        var inbound = MakeMessage() with
        {
            From = new ChannelAddress(ChannelType.WhatsApp, "US.13491208655302741918"),
            AlternateAddresses = [phone],
            ProfileName = "Sheena",
        };
        var contact = MakeContact("bsuid-contact");
        _contactResolver.ResolveAsync(
                TenantId,
                Arg.Is<ContactIdentity>(i =>
                    i.Address == inbound.From && i.AlternateAddresses.SequenceEqual(new[] { phone }) && i.DisplayName == "Sheena"),
                Arg.Any<CancellationToken>())
            .Returns(contact);
        var context = new PipelineContext { InboundMessage = inbound, TenantId = TenantId, Channel = Channel };

        var result = await new ContactResolutionStep(_contactResolver).ExecuteAsync(context, CancellationToken.None);

        result.Contact.Should().BeSameAs(contact);
    }

    // ─── A WebChat session's message (design D9) ───

    private static readonly ChannelAddress SessionAddress = new(ChannelType.WebChat, "webchat-session");

    private static InboundMessage MakeBoundMessage(EntityId contactId, EntityId conversationId) =>
        new(SessionAddress, new MessageEnvelope([]), "ext-bound", DateTimeOffset.UtcNow)
        {
            SessionBinding = new InboundSessionBinding(contactId, conversationId, SessionAddress),
        };

    private static Conversation MakeWebChatConversation(EntityId contactId, ConversationState state = ConversationState.Queued) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = TenantId,
            ContactId = contactId,
            Channel = ChannelType.WebChat,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private Task<PipelineContext> ResolveBoundAsync(Contact contact, EntityId conversationId) =>
        new ConversationResolutionStep(_conversationStore, _lifecycleService, _clock).ExecuteAsync(
            new PipelineContext
            {
                InboundMessage = MakeBoundMessage(contact.ContactId, conversationId),
                TenantId = TenantId,
                Channel = ChannelType.WebChat,
                Contact = contact,
            },
            CancellationToken.None);

    private Conversation GivenTheSessionOpensANewConversation(Contact contact)
    {
        var created = MakeWebChatConversation(contact.ContactId);
        _lifecycleService.CreateForWebChatSessionAsync(TenantId, contact.ContactId, SessionAddress, Arg.Any<CancellationToken>()).Returns(created);
        return created;
    }

    private async Task AssertNoContactWideReadAsync() =>
        await _conversationStore.DidNotReceiveWithAnyArgs().FindActiveByContactAsync(default, default, default, default);

    [Fact]
    public async Task ContactResolutionStep_ShouldReadTheBoundContactById_WhenTheMessageIsBound()
    {
        var contact = MakeContact();
        _contactResolver.FindByIdAsync(TenantId, contact.ContactId, Arg.Any<CancellationToken>()).Returns(contact);
        var context = new PipelineContext
        {
            InboundMessage = MakeBoundMessage(contact.ContactId, EntityId.New()),
            TenantId = TenantId,
            Channel = ChannelType.WebChat,
        };

        var result = await new ContactResolutionStep(_contactResolver).ExecuteAsync(context, CancellationToken.None);

        result.Contact.Should().BeSameAs(contact);
        await _contactResolver.DidNotReceive().ResolveAsync(TenantId, Arg.Any<ContactIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContactResolutionStep_ShouldResolveByAddress_WhenTheBoundContactIsGone()
    {
        var replacement = MakeContact();
        var gone = EntityId.New();
        _contactResolver.FindByIdAsync(TenantId, gone, Arg.Any<CancellationToken>()).Returns((Contact?)null);
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == SessionAddress), Arg.Any<CancellationToken>()).Returns(replacement);
        var context = new PipelineContext
        {
            InboundMessage = MakeBoundMessage(gone, EntityId.New()),
            TenantId = TenantId,
            Channel = ChannelType.WebChat,
        };

        var result = await new ContactResolutionStep(_contactResolver).ExecuteAsync(context, CancellationToken.None);

        result.Contact.Should().BeSameAs(replacement);
    }

    [Fact]
    public async Task ConversationResolutionStep_ShouldClaimTheSessionsConversation_WhenItIsOpen()
    {
        var contact = MakeContact();
        var own = MakeWebChatConversation(contact.ContactId);
        _conversationStore.GetByIdAsync(TenantId, own.ConversationId, Arg.Any<CancellationToken>()).Returns(own);

        var result = await ResolveBoundAsync(contact, own.ConversationId);

        result.Conversation.Should().BeSameAs(own);
        result.IsNewConversation.Should().BeFalse();
        await AssertNoContactWideReadAsync();
    }

    [Fact]
    public async Task ConversationResolutionStep_ShouldRereadTheSameConversationOnly_WhenItsClaimIsLost()
    {
        var contact = MakeContact();
        var own = MakeWebChatConversation(contact.ContactId);
        var closedSince = MakeWebChatConversation(contact.ContactId, ConversationState.Closed);
        _conversationStore.GetByIdAsync(TenantId, own.ConversationId, Arg.Any<CancellationToken>()).Returns(own, closedSince);
        _conversationStore.TryClaimForInboundAsync(TenantId, own.ConversationId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        var created = GivenTheSessionOpensANewConversation(contact);

        var result = await ResolveBoundAsync(contact, own.ConversationId);

        result.Conversation.Should().BeSameAs(created);
        result.IsNewConversation.Should().BeTrue();
        await _conversationStore.Received(2).GetByIdAsync(TenantId, own.ConversationId, Arg.Any<CancellationToken>());
        await AssertNoContactWideReadAsync();
    }

    [Fact]
    public async Task ConversationResolutionStep_ShouldOpenANewConversation_WhenClaimsAreLostUntilTheLimit()
    {
        var contact = MakeContact();
        var own = MakeWebChatConversation(contact.ContactId);
        _conversationStore.GetByIdAsync(TenantId, own.ConversationId, Arg.Any<CancellationToken>()).Returns(own);
        _conversationStore.TryClaimForInboundAsync(TenantId, own.ConversationId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        var created = GivenTheSessionOpensANewConversation(contact);

        var result = await ResolveBoundAsync(contact, own.ConversationId);

        result.Conversation.Should().BeSameAs(created);
        await _conversationStore.Received(ConversationResolutionStep.MaxResolveAttempts).TryClaimForInboundAsync(
            TenantId, own.ConversationId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConversationResolutionStep_ShouldCloseAndOpenANewConversation_WhenTheSessionsConversationIsResolved()
    {
        var contact = MakeContact();
        var own = MakeWebChatConversation(contact.ContactId, ConversationState.Resolved);
        var closed = MakeWebChatConversation(contact.ContactId, ConversationState.Closed);
        _conversationStore.GetByIdAsync(TenantId, own.ConversationId, Arg.Any<CancellationToken>()).Returns(own, closed);
        _conversationStore.TryCloseResolvedForReplyAsync(TenantId, own.ConversationId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        var created = GivenTheSessionOpensANewConversation(contact);

        var result = await ResolveBoundAsync(contact, own.ConversationId);

        result.ClosedOnReturnId.Should().Be(own.ConversationId);
        result.Conversation.Should().BeSameAs(created);
        result.IsNewConversation.Should().BeTrue();
    }

    [Fact]
    public async Task ConversationResolutionStep_ShouldNotCountTheClose_WhenAnotherDeliveryWonIt()
    {
        var contact = MakeContact();
        var own = MakeWebChatConversation(contact.ContactId, ConversationState.Resolved);
        var closed = MakeWebChatConversation(contact.ContactId, ConversationState.Closed);
        _conversationStore.GetByIdAsync(TenantId, own.ConversationId, Arg.Any<CancellationToken>()).Returns(own, closed);
        _conversationStore.TryCloseResolvedForReplyAsync(TenantId, own.ConversationId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        var created = GivenTheSessionOpensANewConversation(contact);

        var result = await ResolveBoundAsync(contact, own.ConversationId);

        result.ClosedOnReturnId.Should().BeNull("only the delivery that won the close announces it");
        result.Conversation.Should().BeSameAs(created);
    }

    [Theory]
    [InlineData(ConversationState.Queued)]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.Resolved)]
    public async Task ConversationResolutionStep_ShouldNeitherClaimNorClose_WhenTheConversationIsAnotherContacts(ConversationState state)
    {
        var contact = MakeContact();
        var leftBehind = MakeWebChatConversation(EntityId.New(), state);
        _conversationStore.GetByIdAsync(TenantId, leftBehind.ConversationId, Arg.Any<CancellationToken>()).Returns(leftBehind);
        var created = GivenTheSessionOpensANewConversation(contact);

        var result = await ResolveBoundAsync(contact, leftBehind.ConversationId);

        result.Conversation.Should().BeSameAs(created);
        result.IsNewConversation.Should().BeTrue();
        result.ClosedOnReturnId.Should().BeNull();
        await _conversationStore.DidNotReceiveWithAnyArgs().TryClaimForInboundAsync(default, default, default, default);
        await _conversationStore.DidNotReceiveWithAnyArgs().TryCloseResolvedForReplyAsync(default, default, default, default);
    }

    [Theory]
    [InlineData(ConversationState.Closed)]
    [InlineData(ConversationState.Abandoned)]
    public async Task ConversationResolutionStep_ShouldOpenANewConversation_WhenTheSessionsConversationEnded(ConversationState state)
    {
        var contact = MakeContact();
        var ended = MakeWebChatConversation(contact.ContactId, state);
        _conversationStore.GetByIdAsync(TenantId, ended.ConversationId, Arg.Any<CancellationToken>()).Returns(ended);
        var created = GivenTheSessionOpensANewConversation(contact);

        var result = await ResolveBoundAsync(contact, ended.ConversationId);

        result.Conversation.Should().BeSameAs(created);
        result.ClosedOnReturnId.Should().BeNull();
        await AssertNoContactWideReadAsync();
    }

    [Fact]
    public async Task ConversationResolutionStep_ShouldOpenANewConversation_WhenTheSessionsConversationIsGone()
    {
        var contact = MakeContact();
        var gone = EntityId.New();
        _conversationStore.GetByIdAsync(TenantId, gone, Arg.Any<CancellationToken>()).Returns((Conversation?)null);
        var created = GivenTheSessionOpensANewConversation(contact);

        var result = await ResolveBoundAsync(contact, gone);

        result.Conversation.Should().BeSameAs(created);
        await AssertNoContactWideReadAsync();
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

        var step = new ConversationResolutionStep(_conversationStore, _lifecycleService, _clock);
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

        var step = new ConversationResolutionStep(_conversationStore, _lifecycleService, _clock);
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

        await _messageStore.Received(1).InsertInboundIfAbsentAsync(result.PersistedMessage, Arg.Any<CancellationToken>());
        result.IsDuplicate.Should().BeFalse();
    }

    [Fact]
    public async Task Pipeline_ShouldReportStoredMessage_WhenConcurrentIdenticalDeliveryWonTheInsert()
    {
        // Both deliveries passed DeduplicateStep before either inserted; the store keeps the first one.
        var inbound = MakeMessage("race-ext");
        var contact = MakeContact();
        var conversation = MakeConversation(contact.ContactId);
        var winner = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = EntityId.New(),
            TenantId = TenantId,
            Direction = MessageDirection.Inbound,
            Channel = Channel,
            Content = new MessageEnvelope([]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = "race-ext",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _messageStore.FindByExternalIdAsync(TenantId, "race-ext", Arg.Any<CancellationToken>()).ReturnsNull();
        _messageStore.InsertInboundIfAbsentAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(winner);
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>()).Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(conversation);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.MessageId.Should().Be(winner.MessageId);
        result.ConversationId.Should().Be(winner.ConversationId);
        result.IsNewConversation.Should().BeFalse();
        result.IsDuplicate.Should().BeTrue("the caller must not repeat the winning delivery's side effects");
        await _messageStore.DidNotReceive().SaveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _conversationStore.DidNotReceiveWithAnyArgs().DeleteIfNoMessagesAsync(default, default, default);
    }

    [Fact]
    public async Task Pipeline_ShouldDiscardConversationItOpened_WhenConcurrentIdenticalDeliveryWonTheInsert()
    {
        var inbound = MakeMessage("race-new");
        var contact = MakeContact();
        var opened = MakeConversation(contact.ContactId, isNew: true);
        var winner = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = EntityId.New(),
            TenantId = TenantId,
            Direction = MessageDirection.Inbound,
            Channel = Channel,
            Content = new MessageEnvelope([]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = "race-new",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _messageStore.FindByExternalIdAsync(TenantId, "race-new", Arg.Any<CancellationToken>()).ReturnsNull();
        _messageStore.InsertInboundIfAbsentAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(winner);
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>()).Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .ReturnsNull();
        _lifecycleService.CreateAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>()).Returns(opened);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.IsDuplicate.Should().BeTrue();
        result.IsNewConversation.Should().BeFalse();
        result.ConversationId.Should().Be(winner.ConversationId);
        await _conversationStore.Received(1).DeleteIfNoMessagesAsync(TenantId, opened.ConversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ShouldFlagDuplicate_WhenExternalIdIsAlreadyStored()
    {
        var inbound = MakeMessage("replayed-ext");
        var contact = MakeContact();
        var stored = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = EntityId.New(),
            TenantId = TenantId,
            Direction = MessageDirection.Inbound,
            Channel = Channel,
            Content = new MessageEnvelope([]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = "replayed-ext",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _messageStore.FindByExternalIdAsync(TenantId, "replayed-ext", Arg.Any<CancellationToken>()).Returns(stored);
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>()).Returns(contact);

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.IsDuplicate.Should().BeTrue();
        result.MessageId.Should().Be(stored.MessageId);
        result.ConversationId.Should().Be(stored.ConversationId);
        await _messageStore.DidNotReceiveWithAnyArgs().InsertInboundIfAbsentAsync(default!, default);
    }

    [Fact]
    public async Task ProcessAsync_ShouldNotFlagDuplicate_WhenMessageIsNew()
    {
        var inbound = MakeMessage("fresh-ext");
        var contact = MakeContact();
        _messageStore.FindByExternalIdAsync(TenantId, "fresh-ext", Arg.Any<CancellationToken>()).ReturnsNull();
        _contactResolver.ResolveAsync(TenantId, Arg.Is<ContactIdentity>(i => i.Address == inbound.From), Arg.Any<CancellationToken>()).Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(MakeConversation(contact.ContactId));

        var result = await _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        result.IsDuplicate.Should().BeFalse();
    }

    // ── conversations-close-after-inactivity task 2.3 (design D7, D8; audit round 4 F1) ─────────────────────────

    [Fact]
    public async Task ConversationResolutionStep_ShouldOpenANewConversation_WhenEveryClaimLoses()
    {
        var contact = MakeContact();
        var existing = MakeConversation(contact.ContactId);
        var created = MakeConversation(contact.ContactId);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>()).Returns(existing);
        _conversationStore.TryClaimForInboundAsync(TenantId, existing.ConversationId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _lifecycleService.CreateAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>()).Returns(created);
        var context = new PipelineContext { InboundMessage = MakeMessage(), TenantId = TenantId, Channel = Channel, Contact = contact };

        var result = await new ConversationResolutionStep(_conversationStore, _lifecycleService, _clock).ExecuteAsync(context, CancellationToken.None);

        result.Conversation.Should().BeSameAs(created, "a message never lands in a row whose claim it could not win");
        result.IsNewConversation.Should().BeTrue();
        await _conversationStore.Received(ConversationResolutionStep.MaxResolveAttempts).TryClaimForInboundAsync(
            TenantId, existing.ConversationId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConversationResolutionStep_ShouldCloseOnlyOneResolvedRow_WhenTheContactHasTwo()
    {
        var contact = MakeContact();
        var first = MakeConversation(contact.ContactId);
        first.State = ConversationState.Resolved;
        var second = MakeConversation(contact.ContactId);
        second.State = ConversationState.Resolved;
        var created = MakeConversation(contact.ContactId);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>()).Returns(first, second);
        _conversationStore.TryCloseResolvedForReplyAsync(TenantId, Arg.Any<EntityId>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _lifecycleService.CreateAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>()).Returns(created);
        var context = new PipelineContext { InboundMessage = MakeMessage(), TenantId = TenantId, Channel = Channel, Contact = contact };

        var result = await new ConversationResolutionStep(_conversationStore, _lifecycleService, _clock).ExecuteAsync(context, CancellationToken.None);

        result.ClosedOnReturnId.Should().Be(first.ConversationId, "one delivery owns at most one return close");
        result.Conversation.Should().BeSameAs(created);
        await _conversationStore.Received(1).TryCloseResolvedForReplyAsync(
            TenantId, Arg.Any<EntityId>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ShouldWrapTheFailureWithTheClosedId_WhenTheInsertThrowsAfterAWonReturnClose()
    {
        var inbound = MakeMessage("f1-ext");
        var contact = MakeContact();
        var resolved = MakeConversation(contact.ContactId);
        resolved.State = ConversationState.Resolved;
        var created = MakeConversation(contact.ContactId);
        _messageStore.FindByExternalIdAsync(TenantId, "f1-ext", Arg.Any<CancellationToken>()).ReturnsNull();
        _contactResolver.ResolveAsync(TenantId, Arg.Any<ContactIdentity>(), Arg.Any<CancellationToken>()).Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(resolved, [null]);
        _conversationStore.TryCloseResolvedForReplyAsync(TenantId, resolved.ConversationId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _lifecycleService.CreateAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>()).Returns(created);
        var failure = new InvalidOperationException("database unavailable");
        _messageStore.InsertInboundIfAbsentAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns<Message>(_ => throw failure);

        var act = () => _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        var thrown = (await act.Should().ThrowExactlyAsync<InboundReturnClosedException>()).Which;
        thrown.ClosedOnReturnId.Should().Be(resolved.ConversationId);
        thrown.TenantId.Should().Be(TenantId);
        thrown.InnerException.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task ProcessAsync_ShouldPropagateTheFailureUnwrapped_WhenNoReturnCloseWasWon()
    {
        var inbound = MakeMessage("plain-fail");
        var contact = MakeContact();
        _messageStore.FindByExternalIdAsync(TenantId, "plain-fail", Arg.Any<CancellationToken>()).ReturnsNull();
        _contactResolver.ResolveAsync(TenantId, Arg.Any<ContactIdentity>(), Arg.Any<CancellationToken>()).Returns(contact);
        _conversationStore.FindActiveByContactAsync(TenantId, contact.ContactId, Channel, Arg.Any<CancellationToken>())
            .Returns(MakeConversation(contact.ContactId));
        _messageStore.InsertInboundIfAbsentAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Message>(_ => throw new InvalidOperationException("database unavailable"));

        var act = () => _pipeline.ProcessAsync(inbound, TenantId, Channel, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }
}
