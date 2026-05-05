using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound.Middlewares;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Routing.Inbound.Tests;

public class LastAgentMiddlewareTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId ContactId = EntityId.From("contact-1");

    private static RoutingContext MakeContext() => new(
        Conversation: new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = ContactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Contact: new Contact
        {
            ContactId = ContactId,
            TenantId = Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Channel: ChannelType.WebChat,
        InitialMessage: null,
        TenantId: Tenant);

    private static Conversation MakeClosedConversation(EntityId agentId, DateTimeOffset closedAt)
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = ContactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Closed,
            CreatedAt = closedAt.AddHours(-1),
            ClosedAt = closedAt,
        };
        conv.Owner = ConversationOwner.ForAgent(agentId);
        return conv;
    }

    [Fact]
    public async Task RouteAsync_ShouldSetPreferredAgentId_WhenPreviousConversationWithinWindow()
    {
        var store = Substitute.For<IConversationStore>();
        var agentId = EntityId.From("agent-99");
        var recentConv = MakeClosedConversation(agentId, DateTimeOffset.UtcNow.AddHours(-1));

        store.ListAsync(Tenant, Arg.Any<ConversationQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Conversation>([recentConv], 1, 1, 10));

        var options = Options.Create(new InboundRoutingOptions { EnableLastAgentRouting = true });
        var middleware = new LastAgentMiddleware(store, options);
        var context = MakeContext();

        var baseResult = new RouteResult(EntityId.From("q-1"), MessagePriority.Normal, null, null);
        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        result.Should().NotBeNull();
        result!.PreferredAgentId.Should().NotBeNull();
        result.PreferredAgentId!.Value.Value.Should().Be("agent-99");
    }

    [Fact]
    public async Task RouteAsync_ShouldNotSetPreferredAgent_WhenNoPreviousConversations()
    {
        var store = Substitute.For<IConversationStore>();
        store.ListAsync(Tenant, Arg.Any<ConversationQuery>(), Arg.Any<CancellationToken>())
            .Returns(PagedResult<Conversation>.Empty(1, 10));

        var options = Options.Create(new InboundRoutingOptions { EnableLastAgentRouting = true });
        var middleware = new LastAgentMiddleware(store, options);
        var context = MakeContext();

        var baseResult = new RouteResult(EntityId.From("q-1"), MessagePriority.Normal, null, null);
        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        result.Should().NotBeNull();
        result!.PreferredAgentId.Should().BeNull();
    }

    [Fact]
    public async Task RouteAsync_ShouldNotSetPreferredAgent_WhenPreviousConversationOutsideWindow()
    {
        var store = Substitute.For<IConversationStore>();
        var agentId = EntityId.From("agent-old");
        var oldConv = MakeClosedConversation(agentId, DateTimeOffset.UtcNow.AddHours(-100));

        store.ListAsync(Tenant, Arg.Any<ConversationQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Conversation>([oldConv], 1, 1, 10));

        var options = Options.Create(new InboundRoutingOptions
        {
            EnableLastAgentRouting = true,
            LastAgentWindow = TimeSpan.FromHours(72),
        });
        var middleware = new LastAgentMiddleware(store, options);
        var context = MakeContext();

        var baseResult = new RouteResult(EntityId.From("q-1"), MessagePriority.Normal, null, null);
        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        result!.PreferredAgentId.Should().BeNull();
    }

    [Fact]
    public async Task RouteAsync_ShouldSkipLookup_WhenLastAgentRoutingDisabled()
    {
        var store = Substitute.For<IConversationStore>();

        var options = Options.Create(new InboundRoutingOptions { EnableLastAgentRouting = false });
        var middleware = new LastAgentMiddleware(store, options);
        var context = MakeContext();

        var baseResult = new RouteResult(EntityId.From("q-1"), MessagePriority.Normal, null, null);
        await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        await store.DidNotReceive().ListAsync(Arg.Any<TenantId>(), Arg.Any<ConversationQuery>(), Arg.Any<CancellationToken>());
    }
}
