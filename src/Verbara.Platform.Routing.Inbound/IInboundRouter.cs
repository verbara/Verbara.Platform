namespace Verbara.Platform.Routing.Inbound;

public interface IInboundRouter
{
    Task<RouteResult> RouteAsync(RoutingContext context, CancellationToken ct);
}
