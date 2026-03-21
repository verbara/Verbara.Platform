using Asterisk.Platform.Core;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Routing.Inbound.Middlewares;

public sealed class ChannelQueueMappingMiddleware : InboundRoutingMiddlewareBase
{
    private readonly InboundRoutingOptions _options;

    public ChannelQueueMappingMiddleware(IOptions<InboundRoutingOptions> options)
    {
        _options = options.Value;
    }

    public override async Task<RouteResult?> RouteAsync(
        RoutingContext context,
        Func<Task<RouteResult?>> continuation,
        CancellationToken ct)
    {
        if (!_options.ChannelQueueMapping.TryGetValue(context.Channel, out var queueId))
            return await continuation().ConfigureAwait(false);

        var downstream = await continuation().ConfigureAwait(false);

        if (downstream is not null)
            return downstream with { QueueId = queueId };

        return new RouteResult(
            QueueId: queueId,
            Priority: MessagePriority.Normal,
            PreferredAgentId: null,
            Metadata: null);
    }
}
