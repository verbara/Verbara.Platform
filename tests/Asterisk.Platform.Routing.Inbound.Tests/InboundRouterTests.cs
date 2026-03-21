using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Routing.Inbound;

namespace Asterisk.Platform.Routing.Inbound.Tests;

public class InboundRouterTests
{
    private static RoutingContext MakeContext() => new(
        Conversation: new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = new TenantId("t1"),
            ContactId = EntityId.New(),
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Contact: new Contact
        {
            ContactId = EntityId.New(),
            TenantId = new TenantId("t1"),
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Channel: ChannelType.WebChat,
        InitialMessage: null,
        TenantId: new TenantId("t1"));

    [Fact]
    public async Task RouteAsync_ShouldExecuteMiddlewareChainInOrder_WhenMultipleMiddlewares()
    {
        var order = new List<int>();

        var m1 = new TrackingMiddleware(1, order, shortCircuit: false);
        var m2 = new TrackingMiddleware(2, order, shortCircuit: false);
        var m3 = new ProducingMiddleware(EntityId.From("q1"));

        var router = new InboundRouter([m1, m2, m3]);
        var result = await router.RouteAsync(MakeContext(), CancellationToken.None);

        order.Should().Equal(1, 2);
        result.QueueId.Value.Should().Be("q1");
    }

    [Fact]
    public async Task RouteAsync_ShouldShortCircuit_WhenFirstMiddlewareReturnsResult()
    {
        var order = new List<int>();

        var m1 = new TrackingMiddleware(1, order, shortCircuit: true);
        var m2 = new TrackingMiddleware(2, order, shortCircuit: false);

        var router = new InboundRouter([m1, m2]);
        var result = await router.RouteAsync(MakeContext(), CancellationToken.None);

        order.Should().Equal(1);
        result.QueueId.Value.Should().Be("short-circuit");
    }

    [Fact]
    public async Task RouteAsync_ShouldThrowInvalidOperationException_WhenNoMiddlewareReturnsResult()
    {
        var router = new InboundRouter([]);

        var act = () => router.RouteAsync(MakeContext(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class TrackingMiddleware : InboundRoutingMiddlewareBase
    {
        private readonly int _id;
        private readonly List<int> _order;
        private readonly bool _shortCircuit;

        public TrackingMiddleware(int id, List<int> order, bool shortCircuit)
        {
            _id = id;
            _order = order;
            _shortCircuit = shortCircuit;
        }

        public override async Task<RouteResult?> RouteAsync(
            RoutingContext context, Func<Task<RouteResult?>> continuation, CancellationToken ct)
        {
            _order.Add(_id);
            if (_shortCircuit)
                return new RouteResult(EntityId.From("short-circuit"), MessagePriority.Normal, null, null);
            return await continuation().ConfigureAwait(false);
        }
    }

    private sealed class ProducingMiddleware : InboundRoutingMiddlewareBase
    {
        private readonly EntityId _queueId;

        public ProducingMiddleware(EntityId queueId) => _queueId = queueId;

        public override Task<RouteResult?> RouteAsync(
            RoutingContext context, Func<Task<RouteResult?>> continuation, CancellationToken ct) =>
            Task.FromResult<RouteResult?>(new RouteResult(_queueId, MessagePriority.Normal, null, null));
    }
}
