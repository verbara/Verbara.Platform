using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Routing.Inbound.Middlewares;

public sealed class BusinessHoursMiddleware : InboundRoutingMiddlewareBase
{
    private readonly IQueueStore _queueStore;
    private readonly IClock _clock;

    public BusinessHoursMiddleware(IQueueStore queueStore, IClock clock)
    {
        _queueStore = queueStore;
        _clock = clock;
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

        if (queue?.Hours is null)
            return result;

        var utcNow = _clock.UtcNow;

        if (!queue.Hours.IsOpen(utcNow))
        {
            if (queue.OverflowRule is not null)
                return result with { QueueId = queue.OverflowRule.OverflowQueueId };

            throw new InvalidOperationException(
                $"Queue '{result.QueueId}' is outside business hours and has no overflow rule configured.");
        }

        return result;
    }
}
