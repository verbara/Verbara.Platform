namespace Verbara.Platform.Routing.Inbound;

public abstract class InboundRoutingMiddlewareBase
{
    public abstract Task<RouteResult?> RouteAsync(
        RoutingContext context,
        Func<Task<RouteResult?>> continuation,
        CancellationToken ct);
}
