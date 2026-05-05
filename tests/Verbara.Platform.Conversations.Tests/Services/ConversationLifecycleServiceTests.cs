using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using NSubstitute;

namespace Verbara.Platform.Conversations.Tests.Services;

public class ConversationLifecycleServiceTests
{
    private readonly IConversationStore _conversationStore = Substitute.For<IConversationStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly DefaultConversationLifecycleService _sut;

    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId ContactId = EntityId.From("contact-1");
    private static readonly DateTimeOffset Now = new(2026, 3, 21, 12, 0, 0, TimeSpan.Zero);

    public ConversationLifecycleServiceTests()
    {
        _clock.UtcNow.Returns(Now);
        _sut = new DefaultConversationLifecycleService(_conversationStore, _clock);
    }

    [Fact]
    public async Task CreateAsync_ShouldCreateConversation_WithQueuedState()
    {
        var result = await _sut.CreateAsync(Tenant, ContactId, ChannelType.WhatsApp, CancellationToken.None);

        result.State.Should().Be(ConversationState.Queued);
    }

    [Fact]
    public async Task CreateAsync_ShouldSetChannel()
    {
        var result = await _sut.CreateAsync(Tenant, ContactId, ChannelType.Sms, CancellationToken.None);

        result.Channel.Should().Be(ChannelType.Sms);
    }

    [Fact]
    public async Task CreateAsync_ShouldSetContactId()
    {
        var result = await _sut.CreateAsync(Tenant, ContactId, ChannelType.Email, CancellationToken.None);

        result.ContactId.Should().Be(ContactId);
    }

    [Fact]
    public async Task CreateAsync_ShouldSaveConversation()
    {
        var result = await _sut.CreateAsync(Tenant, ContactId, ChannelType.Voice, CancellationToken.None);

        await _conversationStore.Received(1).SaveAsync(result, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_ShouldUpdateState_WhenValid()
    {
        var conversation = BuildConversation(ConversationState.Queued);
        _conversationStore.GetByIdAsync(Tenant, conversation.ConversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);

        await _sut.TransitionAsync(Tenant, conversation.ConversationId, ConversationState.Offered, CancellationToken.None);

        conversation.State.Should().Be(ConversationState.Offered);
    }

    [Fact]
    public async Task TransitionAsync_ShouldThrow_WhenInvalidTransition()
    {
        var conversation = BuildConversation(ConversationState.Closed);
        _conversationStore.GetByIdAsync(Tenant, conversation.ConversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);

        var act = async () => await _sut.TransitionAsync(
            Tenant, conversation.ConversationId, ConversationState.Active, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CloseAsync_ShouldSetClosedAt()
    {
        // WrapUp → Closed is a valid transition
        var conversation = BuildConversation(ConversationState.WrapUp);
        _conversationStore.GetByIdAsync(Tenant, conversation.ConversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);

        await _sut.CloseAsync(Tenant, conversation.ConversationId, null, CancellationToken.None);

        conversation.ClosedAt.Should().Be(Now);
    }

    [Fact]
    public async Task CloseAsync_ShouldTransitionToClosed()
    {
        var conversation = BuildConversation(ConversationState.WrapUp);
        _conversationStore.GetByIdAsync(Tenant, conversation.ConversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);

        await _sut.CloseAsync(Tenant, conversation.ConversationId, null, CancellationToken.None);

        conversation.State.Should().Be(ConversationState.Closed);
    }

    private static Conversation BuildConversation(ConversationState state) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = ContactId,
            Channel = ChannelType.WhatsApp,
            State = state,
            CreatedAt = Now,
        };
}
