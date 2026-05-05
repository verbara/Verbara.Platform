using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound.Middlewares;

namespace Verbara.Platform.Routing.Inbound.Tests;

public class BusinessHoursMiddlewareTests
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

    private static Queue MakeQueue(HoursOfOperation? hours, EntityId? overflowQueueId = null) =>
        new()
        {
            QueueId = QueueId,
            TenantId = Tenant,
            Name = "Test Queue",
            Hours = hours,
            OverflowRule = overflowQueueId.HasValue
                ? new QueueOverflowRule { OverflowQueueId = overflowQueueId.Value, OverflowAfterSeconds = 300 }
                : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task RouteAsync_ShouldPassThrough_WhenQueueIsOpen()
    {
        var store = Substitute.For<IQueueStore>();
        var clock = Substitute.For<IClock>();

        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        store.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(MakeQueue(HoursOfOperation.AlwaysOpen()));

        var middleware = new BusinessHoursMiddleware(store, clock);
        var context = MakeContext();
        var baseResult = new RouteResult(QueueId, MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        result.Should().NotBeNull();
        result!.QueueId.Value.Should().Be("q-main");
    }

    [Fact]
    public async Task RouteAsync_ShouldRouteToOverflow_WhenQueueIsClosed()
    {
        var store = Substitute.For<IQueueStore>();
        var clock = Substitute.For<IClock>();

        // Closed hours: schedule nothing (empty = always closed)
        var closedHours = new HoursOfOperation("UTC");
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        store.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(MakeQueue(closedHours, overflowQueueId: OverflowQueueId));

        var middleware = new BusinessHoursMiddleware(store, clock);
        var context = MakeContext();
        var baseResult = new RouteResult(QueueId, MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        result.Should().NotBeNull();
        result!.QueueId.Value.Should().Be("q-overflow");
    }

    [Fact]
    public async Task RouteAsync_ShouldThrow_WhenQueueIsClosedAndNoOverflowRule()
    {
        var store = Substitute.For<IQueueStore>();
        var clock = Substitute.For<IClock>();

        var closedHours = new HoursOfOperation("UTC"); // no schedule = always closed
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        store.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(MakeQueue(closedHours, overflowQueueId: null));

        var middleware = new BusinessHoursMiddleware(store, clock);
        var context = MakeContext();
        var baseResult = new RouteResult(QueueId, MessagePriority.Normal, null, null);

        var act = () => middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RouteAsync_ShouldPassThrough_WhenQueueHasNoHoursConfigured()
    {
        var store = Substitute.For<IQueueStore>();
        var clock = Substitute.For<IClock>();

        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        store.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(MakeQueue(hours: null));

        var middleware = new BusinessHoursMiddleware(store, clock);
        var context = MakeContext();
        var baseResult = new RouteResult(QueueId, MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(baseResult), CancellationToken.None);

        result!.QueueId.Value.Should().Be("q-main");
    }

    [Fact]
    public async Task RouteAsync_ShouldReturnNull_WhenNextReturnsNull()
    {
        var store = Substitute.For<IQueueStore>();
        var clock = Substitute.For<IClock>();

        var middleware = new BusinessHoursMiddleware(store, clock);
        var context = MakeContext();

        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(null), CancellationToken.None);

        result.Should().BeNull();
    }
}
