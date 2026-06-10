using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound.Middlewares;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Resolution;

namespace Verbara.Platform.Routing.Inbound.Tests;

public class ReasonHintMiddlewareTests
{
    private static readonly TenantId Tenant = new("t1");

    private static ReasonHintMiddleware MakeMiddleware(IReasonHintResolver resolver) =>
        new(resolver, NullLogger<ReasonHintMiddleware>.Instance);

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

    private static ReasonHint MakeHint(string reasonPath) => new()
    {
        HintId = EntityId.New(),
        TenantId = Tenant,
        Scope = ReasonHintScope.Channel,
        ScopeRef = "scope",
        ReasonPath = reasonPath,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FakeReasonHintResolver : IReasonHintResolver
    {
        private readonly ReasonHint? _hint;

        public FakeReasonHintResolver(ReasonHint? hint)
        {
            _hint = hint;
        }

        public TenantId? CapturedTenantId { get; private set; }
        public string? CapturedDid { get; private set; }
        public EntityId? CapturedQueueId { get; private set; }
        public ChannelType? CapturedChannel { get; private set; }
        public int CallCount { get; private set; }

        public Task<ReasonHint?> ResolveAsync(
            TenantId tenantId,
            string? did,
            EntityId? queueId,
            ChannelType channel,
            CancellationToken ct)
        {
            CapturedTenantId = tenantId;
            CapturedDid = did;
            CapturedQueueId = queueId;
            CapturedChannel = channel;
            CallCount++;
            return Task.FromResult(_hint);
        }
    }

    [Fact]
    public async Task RouteAsync_ShouldStampReasonPathMetadata_WhenChannelHintExists()
    {
        var resolver = new FakeReasonHintResolver(MakeHint("[\"CITAS\",\"AGENDAR\"]"));
        var middleware = MakeMiddleware(resolver);
        var context = MakeContext(ChannelType.WebChat);
        var downstream = new RouteResult(EntityId.From("q-chat"), MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(
            context,
            () => Task.FromResult<RouteResult?>(downstream),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Metadata.Should().NotBeNull();
        result.Metadata!.Should().ContainKey(TypificationMetadataKeys.ReasonPath)
            .WhoseValue.Should().Be("[\"CITAS\",\"AGENDAR\"]");
        result.QueueId.Value.Should().Be("q-chat");
    }

    [Fact]
    public async Task RouteAsync_ShouldStampUsingResolvedQueue_WhenQueueHintMoreSpecific()
    {
        var resolver = new FakeReasonHintResolver(MakeHint("[\"SOPORTE\"]"));
        var middleware = MakeMiddleware(resolver);
        var context = MakeContext(ChannelType.WebChat);
        var resolvedQueue = EntityId.From("q-resolved");
        var downstream = new RouteResult(resolvedQueue, MessagePriority.Normal, null, null);

        await middleware.RouteAsync(
            context,
            () => Task.FromResult<RouteResult?>(downstream),
            CancellationToken.None);

        resolver.CallCount.Should().Be(1);
        resolver.CapturedQueueId.Should().Be(resolvedQueue);
        resolver.CapturedDid.Should().BeNull();
        resolver.CapturedTenantId.Should().Be(Tenant);
        resolver.CapturedChannel.Should().Be(ChannelType.WebChat);
    }

    [Fact]
    public async Task RouteAsync_ShouldLeaveMetadataNull_WhenNoHint()
    {
        var resolver = new FakeReasonHintResolver(null);
        var middleware = MakeMiddleware(resolver);
        var context = MakeContext(ChannelType.WebChat);
        var downstream = new RouteResult(EntityId.From("q-chat"), MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(
            context,
            () => Task.FromResult<RouteResult?>(downstream),
            CancellationToken.None);

        result.Should().BeSameAs(downstream);
        result!.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task RouteAsync_ShouldNotOverrideExistingDownstreamMetadataKeys_WhenMerging()
    {
        var resolver = new FakeReasonHintResolver(MakeHint("[\"RESOLVED\"]"));
        var middleware = MakeMiddleware(resolver);
        var context = MakeContext(ChannelType.WebChat);
        var existingMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TypificationMetadataKeys.ReasonPath] = "[\"PRE_EXISTING\"]",
            ["other"] = "keep-me",
        };
        var downstream = new RouteResult(
            EntityId.From("q-chat"),
            MessagePriority.Normal,
            null,
            existingMetadata);

        var result = await middleware.RouteAsync(
            context,
            () => Task.FromResult<RouteResult?>(downstream),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Metadata.Should().NotBeNull();
        result.Metadata![TypificationMetadataKeys.ReasonPath].Should().Be("[\"PRE_EXISTING\"]");
        result.Metadata["other"].Should().Be("keep-me");
    }

    [Fact]
    public async Task RouteAsync_ShouldReturnNull_WhenDownstreamReturnsNull()
    {
        var resolver = new FakeReasonHintResolver(MakeHint("[\"CITAS\"]"));
        var middleware = MakeMiddleware(resolver);
        var context = MakeContext(ChannelType.WebChat);

        var result = await middleware.RouteAsync(
            context,
            () => Task.FromResult<RouteResult?>(null),
            CancellationToken.None);

        result.Should().BeNull();
        resolver.CallCount.Should().Be(0);
    }

    private sealed class ThrowingReasonHintResolver : IReasonHintResolver
    {
        public Task<ReasonHint?> ResolveAsync(
            TenantId tenantId,
            string? did,
            EntityId? queueId,
            ChannelType channel,
            CancellationToken ct) =>
            throw new InvalidOperationException("transient store failure");
    }

    [Fact]
    public async Task RouteAsync_ShouldReturnDownstreamUnchanged_WhenResolverThrows()
    {
        var middleware = MakeMiddleware(new ThrowingReasonHintResolver());
        var context = MakeContext(ChannelType.WebChat);
        var downstream = new RouteResult(EntityId.From("q-chat"), MessagePriority.Normal, null, null);

        var result = await middleware.RouteAsync(
            context,
            () => Task.FromResult<RouteResult?>(downstream),
            CancellationToken.None);

        // Best-effort capture must never fail the route: downstream returned verbatim,
        // no reasonPath metadata stamped.
        result.Should().BeSameAs(downstream);
        result!.Metadata.Should().BeNull();
    }
}
