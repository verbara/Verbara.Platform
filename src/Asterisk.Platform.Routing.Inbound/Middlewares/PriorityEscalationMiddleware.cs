using Asterisk.Platform.Core;

namespace Asterisk.Platform.Routing.Inbound.Middlewares;

public sealed class PriorityEscalationMiddleware : InboundRoutingMiddlewareBase
{
    private static readonly HashSet<string> _elevatedSegments =
        new(StringComparer.OrdinalIgnoreCase) { "vip", "premium" };

    public override async Task<RouteResult?> RouteAsync(
        RoutingContext context,
        Func<Task<RouteResult?>> continuation,
        CancellationToken ct)
    {
        var result = await continuation().ConfigureAwait(false);

        if (result is null)
            return null;

        if (context.Contact.Segment is not null && _elevatedSegments.Contains(context.Contact.Segment))
            return result with { Priority = MessagePriority.High };

        return result;
    }
}
