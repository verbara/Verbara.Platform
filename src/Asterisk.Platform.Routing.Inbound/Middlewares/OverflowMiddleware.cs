using Asterisk.Platform.Queues;

namespace Asterisk.Platform.Routing.Inbound.Middlewares;

public sealed class OverflowMiddleware : InboundRoutingMiddlewareBase
{
    private readonly IQueueStore _queueStore;

    public OverflowMiddleware(IQueueStore queueStore)
    {
        _queueStore = queueStore;
    }

    public override async Task<RouteResult?> RouteAsync(
        RoutingContext context,
        Func<Task<RouteResult?>> continuation,
        CancellationToken ct)
    {
        var result = await continuation().ConfigureAwait(false);

        if (result is null)
            return null;

        var queue = await _queueStore.GetByIdAsync(context.TenantId, result.QueueId, ct).ConfigureAwait(false);

        if (queue is null)
            return result;

        if (queue.MaxWaiting.HasValue && queue.OverflowRule is not null)
        {
            // We treat MaxWaiting as a threshold: if configured (non-null), apply overflow
            // In a real system this would check actual waiting count; here we use MaxWaiting == 0 as "at capacity"
            if (queue.MaxWaiting.Value == 0)
                return result with { QueueId = queue.OverflowRule.OverflowQueueId };
        }

        return result;
    }
}
