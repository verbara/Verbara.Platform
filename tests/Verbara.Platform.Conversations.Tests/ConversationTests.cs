using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Tests;

public class ConversationTests
{
    [Fact]
    public void Constructor_ShouldCreateConversation_WhenValidInput()
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.From("conv-001"),
            TenantId = new TenantId("t1"),
            ContactId = EntityId.From("c-001"),
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        conv.State.Should().Be(ConversationState.Queued);
        conv.Sessions.Should().BeEmpty();
        conv.Owner.Should().BeNull();
    }

    [Fact]
    public void TransitionTo_ShouldUpdateState_WhenValidTransition()
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.From("conv-001"),
            TenantId = new TenantId("t1"),
            ContactId = EntityId.From("c-001"),
            Channel = ChannelType.WebChat,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        conv.TransitionTo(ConversationState.Offered);

        conv.State.Should().Be(ConversationState.Offered);
    }

    [Fact]
    public void TransitionTo_ShouldThrow_WhenInvalidTransition()
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.From("conv-001"),
            TenantId = new TenantId("t1"),
            ContactId = EntityId.From("c-001"),
            Channel = ChannelType.Voice,
            State = ConversationState.Closed,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var act = () => conv.TransitionTo(ConversationState.Active);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TransitionTo_ShouldSetClosedAt_WhenTerminal()
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.From("conv-001"),
            TenantId = new TenantId("t1"),
            ContactId = EntityId.From("c-001"),
            Channel = ChannelType.Email,
            State = ConversationState.WrapUp,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        conv.TransitionTo(ConversationState.Closed);

        conv.ClosedAt.Should().NotBeNull();
    }

    [Fact]
    public void AddSession_ShouldAppendSession()
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.From("conv-001"),
            TenantId = new TenantId("t1"),
            ContactId = EntityId.From("c-001"),
            Channel = ChannelType.Sms,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var session = new ConversationSession
        {
            SessionId = EntityId.From("s-001"),
            Channel = ChannelType.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow,
        };

        conv.AddSession(session);

        conv.Sessions.Should().HaveCount(1);
        conv.Sessions[0].Channel.Should().Be(ChannelType.WhatsApp);
    }
}
