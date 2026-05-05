using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound.Middlewares;

namespace Verbara.Platform.Routing.Inbound.Tests;

public class PriorityEscalationMiddlewareTests
{
    private static readonly TenantId Tenant = new("t1");

    private static RoutingContext MakeContext(string? segment) => new(
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
            Segment = segment,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Channel: ChannelType.WebChat,
        InitialMessage: null,
        TenantId: Tenant);

    private static Task<RouteResult?> MakeNext(MessagePriority priority = MessagePriority.Normal) =>
        Task.FromResult<RouteResult?>(new RouteResult(EntityId.From("q-1"), priority, null, null));

    [Theory]
    [InlineData("vip")]
    [InlineData("VIP")]
    [InlineData("premium")]
    [InlineData("Premium")]
    public async Task RouteAsync_ShouldSetHighPriority_WhenContactIsElevatedSegment(string segment)
    {
        var middleware = new PriorityEscalationMiddleware();
        var context = MakeContext(segment);

        var result = await middleware.RouteAsync(context, () => MakeNext(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Priority.Should().Be(MessagePriority.High);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("basic")]
    [InlineData(null)]
    public async Task RouteAsync_ShouldNotChangePriority_WhenContactIsNormalSegment(string? segment)
    {
        var middleware = new PriorityEscalationMiddleware();
        var context = MakeContext(segment);

        var result = await middleware.RouteAsync(context, () => MakeNext(MessagePriority.Normal), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Priority.Should().Be(MessagePriority.Normal);
    }

    [Fact]
    public async Task RouteAsync_ShouldReturnNull_WhenNextReturnsNull()
    {
        var middleware = new PriorityEscalationMiddleware();
        var context = MakeContext("vip");

        var result = await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(null), CancellationToken.None);

        result.Should().BeNull();
    }
}
