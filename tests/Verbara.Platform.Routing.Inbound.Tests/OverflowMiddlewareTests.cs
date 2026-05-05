using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound.Middlewares;

namespace Verbara.Platform.Routing.Inbound.Tests;

public class OverflowMiddlewareTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId QueueId = EntityId.From("q-main");
    private static readonly EntityId OverflowQueueId = EntityId.From("q-overflow");

    private static RoutingContext MakeContext() => new(
        Conversation: new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WebChat,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Contact: new Contact
        {
            ContactId = EntityId.New(),
            TenantId = Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Channel: ChannelType.WebChat,
        InitialMessage: null,
        TenantId: Tenant);

    private static Queue MakeQueue(int? maxWaiting, EntityId? overflowQueueId = null) =>
        new()
        {
            QueueId = QueueId,
            TenantId = Tenant,
            Name = "Test Queue",
            MaxWaiting = maxWaiting,
            OverflowRule = overflowQueueId.HasValue
                ? new QueueOverflowRule { OverflowQueueId = overflowQueueId.Value, OverflowAfterSeconds = 300 }
                : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task RouteAsync_ShouldReturnOverflowQueue_WhenQueueAtCapacity()
    {
        var store = Substitute.For<IQueueStore>();
        store.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(MakeQueue(maxWaiting: 0, overflowQueueId: OverflowQueueId));

        var middleware = new OverflowMiddleware(store);
        var context = MakeContext();
        var baseResult = new RouteResult(QueueId, MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        result.Should().NotBeNull();
        result!.QueueId.Value.Should().Be("q-overflow");
    }

    [Fact]
    public async Task RouteAsync_ShouldReturnOriginalQueue_WhenQueueHasCapacity()
    {
        var store = Substitute.For<IQueueStore>();
        store.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(MakeQueue(maxWaiting: 10, overflowQueueId: OverflowQueueId));

        var middleware = new OverflowMiddleware(store);
        var context = MakeContext();
        var baseResult = new RouteResult(QueueId, MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        result.Should().NotBeNull();
        result!.QueueId.Value.Should().Be("q-main");
    }

    [Fact]
    public async Task RouteAsync_ShouldReturnOriginalQueue_WhenNoMaxWaitingConfigured()
    {
        var store = Substitute.For<IQueueStore>();
        store.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(MakeQueue(maxWaiting: null));

        var middleware = new OverflowMiddleware(store);
        var context = MakeContext();
        var baseResult = new RouteResult(QueueId, MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        result!.QueueId.Value.Should().Be("q-main");
    }

    [Fact]
    public async Task RouteAsync_ShouldReturnNull_WhenNextReturnsNull()
    {
        var store = Substitute.For<IQueueStore>();

        var middleware = new OverflowMiddleware(store);
        var context = MakeContext();

        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(null), CancellationToken.None);

        result.Should().BeNull();
    }
}
