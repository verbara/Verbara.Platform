using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Routing.Inbound.Middlewares;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Routing.Inbound.Tests;

public class ChannelQueueMappingMiddlewareTests
{
    private static readonly TenantId Tenant = new("t1");

    private static RoutingContext MakeContext(ChannelType channel = ChannelType.WebChat) => new(
        Conversation: new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = EntityId.New(),
            Channel = channel,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Contact: new Contact
        {
            ContactId = EntityId.New(),
            TenantId = Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        },
        Channel: channel,
        InitialMessage: null,
        TenantId: Tenant);

    private static ChannelQueueMappingMiddleware MakeMiddleware(
        Dictionary<ChannelType, EntityId>? mapping = null)
    {
        var options = new InboundRoutingOptions();
        if (mapping is not null)
            options.ChannelQueueMapping = mapping;

        return new ChannelQueueMappingMiddleware(Options.Create(options));
    }

    [Fact]
    public async Task RouteAsync_ShouldMapChannelToQueue_WhenMappingExists()
    {
        var queueId = EntityId.From("q-chat");
        var middleware = MakeMiddleware(new Dictionary<ChannelType, EntityId>
        {
            [ChannelType.WebChat] = queueId,
        });

        var context = MakeContext(ChannelType.WebChat);
        RouteResult? result = null;
        await middleware.RouteAsync(context, () => Task.FromResult<RouteResult?>(null), CancellationToken.None);

        // Should produce result from mapping even when next returns null
        result = await middleware.RouteAsync(
            context,
            () => Task.FromResult<RouteResult?>(null),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.QueueId.Value.Should().Be("q-chat");
    }

    [Fact]
    public async Task RouteAsync_ShouldCallNext_WhenNoMappingForChannel()
    {
        var middleware = MakeMiddleware(); // no mapping
        var context = MakeContext(ChannelType.WebChat);

        var nextCalled = false;
        var expected = new RouteResult(EntityId.From("q-fallback"), MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<RouteResult?>(expected);
        }, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be(expected);
    }

    [Fact]
    public async Task RouteAsync_ShouldOverrideQueueId_WhenMappingExistsAndNextReturnsResult()
    {
        var mappedQueueId = EntityId.From("q-voice-mapped");
        var middleware = MakeMiddleware(new Dictionary<ChannelType, EntityId>
        {
            [ChannelType.Voice] = mappedQueueId,
        });

        var context = MakeContext(ChannelType.Voice);
        var downstreamResult = new RouteResult(EntityId.From("q-other"), MessagePriority.High, null, null);

        var result = await middleware.RouteAsync(
            context,
            () => Task.FromResult<RouteResult?>(downstreamResult),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.QueueId.Value.Should().Be("q-voice-mapped");
        result.Priority.Should().Be(MessagePriority.High);
    }
}
