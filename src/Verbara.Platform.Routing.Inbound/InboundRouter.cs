namespace Verbara.Platform.Routing.Inbound;

public sealed class InboundRouter : IInboundRouter
{
    private readonly IReadOnlyList<InboundRoutingMiddlewareBase> _middlewares;

    public InboundRouter(IReadOnlyList<InboundRoutingMiddlewareBase> middlewares)
    {
        _middlewares = middlewares;
    }

    public async Task<RouteResult> RouteAsync(RoutingContext context, CancellationToken ct)
    {
        var result = await BuildChain(0, context, ct).ConfigureAwait(false);

        return result ?? throw new InvalidOperationException(
            "Routing pipeline produced no result. Ensure at least one middleware returns a RouteResult.");
    }

    private Task<RouteResult?> BuildChain(int index, RoutingContext context, CancellationToken ct)
    {
        if (index >= _middlewares.Count)
            return Task.FromResult<RouteResult?>(null);

        var middleware = _middlewares[index];
        return middleware.RouteAsync(context, () => BuildChain(index + 1, context, ct), ct);
    }
}
