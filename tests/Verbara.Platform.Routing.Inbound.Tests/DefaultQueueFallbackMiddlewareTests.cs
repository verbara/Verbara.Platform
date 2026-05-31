using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound.Middlewares;
using NSubstitute;

namespace Verbara.Platform.Routing.Inbound.Tests;

/// <summary>
/// Tests for <see cref="DefaultQueueFallbackMiddleware"/> — the innermost routing middleware that
/// guarantees an inbound conversation resolves to a queue when no explicit rule upstream did.
/// Without it, <see cref="InboundRouter"/> throws for channels with no ChannelQueueMapping
/// (e.g. WebChat on a fresh SMB tenant). Tier 1: the channel's configured default queue
/// (<c>TenantChannelConfig.Credentials["defaultQueueId"]</c>); Tier 2: the tenant's first active queue.
/// </summary>
public sealed class DefaultQueueFallbackMiddlewareTests
{
    private static readonly TenantId Tenant = new("t-fallback");

    private static RoutingContext Ctx(ChannelType channel = ChannelType.WebChat) => new(
        new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = EntityId.New(),
            Channel = channel,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow },
        channel,
        InitialMessage: null,
        Tenant);

    private static Queue ActiveQueue(string id, string name) =>
        new() { QueueId = EntityId.From(id), TenantId = Tenant, Name = name, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };

    private static TenantChannelConfig ConfigWithDefaultQueue(string queueId) => new()
    {
        TenantId = Tenant,
        Channel = ChannelType.WebChat,
        Credentials = new Dictionary<string, string> { ["defaultQueueId"] = queueId },
    };

    [Fact]
    public async Task RouteAsync_ShouldReturnDownstreamResult_WhenUpstreamResolvedAQueue()
    {
        var channelConfig = Substitute.For<ITenantChannelConfigStore>();
        var queueStore = Substitute.For<IQueueStore>();
        var sut = new DefaultQueueFallbackMiddleware(channelConfig, queueStore);
        var upstream = new RouteResult(EntityId.From("q-upstream"), MessagePriority.High, null, null);

        var result = await sut.RouteAsync(Ctx(), () => Task.FromResult<RouteResult?>(upstream), CancellationToken.None);

        result.Should().Be(upstream);
        await queueStore.DidNotReceiveWithAnyArgs().ListAsync(default!, default!, default);
    }

    [Fact]
    public async Task RouteAsync_ShouldUseChannelConfigDefaultQueue_WhenConfiguredAndActive()
    {
        var channelConfig = Substitute.For<ITenantChannelConfigStore>();
        channelConfig.GetAsync(Tenant, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns(ConfigWithDefaultQueue("q-configured"));
        var queueStore = Substitute.For<IQueueStore>();
        queueStore.GetByIdAsync(Tenant, EntityId.From("q-configured"), Arg.Any<CancellationToken>())
            .Returns(ActiveQueue("q-configured", "Soporte"));
        var sut = new DefaultQueueFallbackMiddleware(channelConfig, queueStore);

        var result = await sut.RouteAsync(Ctx(), () => Task.FromResult<RouteResult?>(null), CancellationToken.None);

        result.Should().NotBeNull();
        result!.QueueId.Value.Should().Be("q-configured");
    }

    [Fact]
    public async Task RouteAsync_ShouldFallBackToFirstActiveQueue_WhenNoChannelConfigDefault()
    {
        var channelConfig = Substitute.For<ITenantChannelConfigStore>();
        channelConfig.GetAsync(Tenant, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns((TenantChannelConfig?)null);
        var queueStore = Substitute.For<IQueueStore>();
        queueStore.ListAsync(Tenant, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Queue>([ActiveQueue("q-first", "Atención General")], totalCount: 1, page: 1, pageSize: 50));
        var sut = new DefaultQueueFallbackMiddleware(channelConfig, queueStore);

        var result = await sut.RouteAsync(Ctx(), () => Task.FromResult<RouteResult?>(null), CancellationToken.None);

        result.Should().NotBeNull();
        result!.QueueId.Value.Should().Be("q-first");
    }

    [Fact]
    public async Task RouteAsync_ShouldFallBackToFirstActiveQueue_WhenConfiguredQueueIsInactiveOrMissing()
    {
        var channelConfig = Substitute.For<ITenantChannelConfigStore>();
        channelConfig.GetAsync(Tenant, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns(ConfigWithDefaultQueue("q-deleted"));
        var queueStore = Substitute.For<IQueueStore>();
        queueStore.GetByIdAsync(Tenant, EntityId.From("q-deleted"), Arg.Any<CancellationToken>())
            .Returns((Queue?)null); // stale id → no longer exists
        queueStore.ListAsync(Tenant, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Queue>([ActiveQueue("q-first", "Atención General")], totalCount: 1, page: 1, pageSize: 50));
        var sut = new DefaultQueueFallbackMiddleware(channelConfig, queueStore);

        var result = await sut.RouteAsync(Ctx(), () => Task.FromResult<RouteResult?>(null), CancellationToken.None);

        result.Should().NotBeNull();
        result!.QueueId.Value.Should().Be("q-first");
    }

    [Fact]
    public async Task RouteAsync_ShouldReturnNull_WhenTenantHasNoActiveQueues()
    {
        var channelConfig = Substitute.For<ITenantChannelConfigStore>();
        channelConfig.GetAsync(Tenant, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns((TenantChannelConfig?)null);
        var queueStore = Substitute.For<IQueueStore>();
        queueStore.ListAsync(Tenant, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Queue>([], totalCount: 0, page: 1, pageSize: 50));
        var sut = new DefaultQueueFallbackMiddleware(channelConfig, queueStore);

        var result = await sut.RouteAsync(Ctx(), () => Task.FromResult<RouteResult?>(null), CancellationToken.None);

        result.Should().BeNull();
    }
}
