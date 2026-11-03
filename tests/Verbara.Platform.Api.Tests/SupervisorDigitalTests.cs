using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public class SupervisorDigitalTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private static readonly TenantId Tenant = new("tenant-1");

    private readonly ConversationOwnershipApiFactory _factory;

    public SupervisorDigitalTests(ConversationOwnershipApiFactory factory) => _factory = factory;

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
        await lifecycleService.CloseAsync(Tenant, conv.ConversationId, CancellationToken.None);

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

    // conversations-close-after-inactivity task 1.3 (design D3): the generic close keeps refusing an agent-held or
    // voice conversation. The supervisor force-close runs the real DefaultConversationLifecycleService over the
    // in-memory stores; an added Active/OnHold/Consulting -> Closed edge would let it close the row with the agent's
    // unit still reserved and nobody told. Consulting has no producer, so it is seeded directly.
    [Theory]
    [InlineData(ConversationState.Active, ChannelType.WebChat)]
    [InlineData(ConversationState.OnHold, ChannelType.WebChat)]
    [InlineData(ConversationState.Consulting, ChannelType.WebChat)]
    [InlineData(ConversationState.Active, ChannelType.Voice)]
    public async Task ForceClose_ShouldReturn400AndChangeNothing_WhenTheConversationIsAgentHeld(
        ConversationState state, ChannelType channel)
    {
        var (_, agent) = _factory.NewAgent("force-close-owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), state, channel: channel);
        _factory.Reserve(agent.AgentId, channel);
        var before = _factory.Load(conversation.ConversationId)!;
        var (stateBefore, ownerBefore, closedBefore, updatedBefore) = (before.State, before.Owner, before.ClosedAt, before.UpdatedAt);
        var loadBefore = _factory.LoadOf(agent.AgentId, channel);
        var events = new ConcurrentQueue<ConversationStateChangedEvent>();
        using var subscription = _factory.Services.GetRequiredService<PlatformEventBus>().Events.Subscribe(e =>
        {
            if (e is ConversationStateChangedEvent changed && changed.ConversationId == conversation.ConversationId.Value)
                events.Enqueue(changed);
        });
        using var supervisor = _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, "force-close-supervisor"));

        using var response = await supervisor.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/close", new { reason = "test" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the generic close refuses {0} → Closed, as on main (D3)", state);
        var after = _factory.Load(conversation.ConversationId)!;
        after.State.Should().Be(stateBefore);
        after.Owner.Should().Be(ownerBefore);
        after.ClosedAt.Should().Be(closedBefore);
        after.UpdatedAt.Should().Be(updatedBefore);
        _factory.LoadOf(agent.AgentId, channel).Should().Be(loadBefore, "a refused close releases nothing");
        events.Should().BeEmpty("a refused close announces nothing");
        _factory.AuditOf(conversation.ConversationId).Should().NotContain(e => e.Action == "conversation.closed");
    }
}
