using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Resolution;

namespace Verbara.Platform.Routing.Inbound.Middlewares;

/// <summary>
/// Post-processing middleware that stamps the captured reason taxonomy
/// (<see cref="TypificationMetadataKeys.ReasonPath"/>) into the resolved route's
/// metadata so the disposition form can be prefilled — the implicit digital
/// capture path (B3). Resolution is continuation-first because the most-specific
/// hint scope (Queue) is only known once the downstream chain has resolved the
/// queue; digital channels carry no DID so resolution falls through Queue → Channel.
/// Existing downstream metadata keys are never overwritten.
/// </summary>
public sealed class ReasonHintMiddleware : InboundRoutingMiddlewareBase
{
    private readonly IReasonHintResolver _resolver;

    public ReasonHintMiddleware(IReasonHintResolver resolver)
    {
        _resolver = resolver;
    }

    public override async Task<RouteResult?> RouteAsync(
        RoutingContext context,
        Func<Task<RouteResult?>> continuation,
        CancellationToken ct)
    {
        var downstream = await continuation().ConfigureAwait(false);
        if (downstream is null)
            return null;

        var hint = await _resolver.ResolveAsync(
            context.TenantId,
            did: null,
            queueId: downstream.QueueId,
            channel: context.Channel,
            ct).ConfigureAwait(false);

        if (hint is null || string.IsNullOrEmpty(hint.ReasonPath))
            return downstream;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (downstream.Metadata is not null)
        {
            foreach (var kv in downstream.Metadata)
                merged[kv.Key] = kv.Value;
        }

        if (!merged.ContainsKey(TypificationMetadataKeys.ReasonPath))
            merged[TypificationMetadataKeys.ReasonPath] = hint.ReasonPath;

        return downstream with { Metadata = merged };
    }
}
