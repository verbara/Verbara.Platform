using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public class SupervisorDigitalTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static Conversation MakeConversation(ConversationState state = ConversationState.Active,
        ChannelType channel = ChannelType.WebChat, string? agentId = null)
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = EntityId.New(),
            Channel = channel,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (agentId is not null)
            conv.Owner = ConversationOwner.ForAgent(EntityId.From(agentId));
        return conv;
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByState()
    {
        var store = new InMemoryConversationStore();
        var active = MakeConversation(ConversationState.Active);
        var closed = MakeConversation(ConversationState.Closed);
        await store.SaveAsync(active, CancellationToken.None);
        await store.SaveAsync(closed, CancellationToken.None);

        var query = new ConversationQuery { State = ConversationState.Active };
        var result = await store.ListAsync(Tenant, query, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].State.Should().Be(ConversationState.Active);
    }

    [Fact]
    public async Task GetConversationMessages_ShouldReturnMessages()
    {
        var store = new InMemoryMessageStore();
        var convId = EntityId.From("conv-1");

        var msg = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = convId,
            TenantId = Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WebChat,
            SenderId = "visitor",
            Content = new MessageEnvelope([new TextBlock("Hello")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(msg, CancellationToken.None);

        var messages = await store.GetConversationMessagesAsync(
            Tenant, convId, 50, 0, CancellationToken.None);

        messages.Should().ContainSingle();
        messages[0].Direction.Should().Be(MessageDirection.Inbound);
    }

    [Fact]
    public async Task CoachingNote_ShouldBeSystemDirection()
    {
        var store = new InMemoryMessageStore();
        var convId = EntityId.From("conv-1");

        var note = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = convId,
            TenantId = Tenant,
            Direction = MessageDirection.System,
            Channel = ChannelType.WebChat,
            SenderId = "supervisor-1",
            Content = new MessageEnvelope([new TextBlock("Ask about their account")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "supervisor:supervisor-1",
        };
        await store.SaveAsync(note, CancellationToken.None);

        var messages = await store.GetConversationMessagesAsync(
            Tenant, convId, 50, 0, CancellationToken.None);

        messages.Should().ContainSingle();
        messages[0].Direction.Should().Be(MessageDirection.System);
        messages[0].CreatedBy.Should().StartWith("supervisor:");
    }

    [Fact]
    public async Task ForceClose_ShouldTransitionToClosed_WhenInWrapUp()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var convStore = new InMemoryConversationStore();

        var conv = MakeConversation(ConversationState.WrapUp);
        await convStore.SaveAsync(conv, CancellationToken.None);

        var lifecycleService = new DefaultConversationLifecycleService(convStore, clock);
        await lifecycleService.CloseAsync(Tenant, conv.ConversationId, wrapUp: null, CancellationToken.None);

        var updated = await convStore.GetByIdAsync(Tenant, conv.ConversationId, CancellationToken.None);
        updated!.State.Should().Be(ConversationState.Closed);
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByChannel()
    {
        var store = new InMemoryConversationStore();
        await store.SaveAsync(MakeConversation(channel: ChannelType.WebChat), CancellationToken.None);
        await store.SaveAsync(MakeConversation(channel: ChannelType.Email), CancellationToken.None);

        var query = new ConversationQuery { Channel = ChannelType.WebChat };
        var result = await store.ListAsync(Tenant, query, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].Channel.Should().Be(ChannelType.WebChat);
    }
}
