using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Routing.Inbound.Middlewares;

/// <summary>
/// Innermost routing middleware: guarantees an inbound conversation resolves to a queue when no
/// explicit rule upstream produced one. Without it, <see cref="InboundRouter"/> throws for channels
/// that have no <c>ChannelQueueMapping</c> entry (e.g. WebChat on a fresh SMB tenant — which is why
/// WebChat conversations were never queued). Resolution order:
/// <list type="number">
///   <item>Tier 1 — the channel's configured default queue
///   (<c>TenantChannelConfig.Credentials["defaultQueueId"]</c>), validated as existing + active.</item>
///   <item>Tier 2 — the tenant's first active queue.</item>
/// </list>
/// Returns <c>null</c> only when the tenant has no active queue at all (genuinely cannot route);
/// the caller logs/skips rather than assigning a bogus owner.
/// </summary>
public sealed class DefaultQueueFallbackMiddleware : InboundRoutingMiddlewareBase
{
    /// <summary>The <see cref="TenantChannelConfig.Credentials"/> key holding an explicit default queue id.</summary>
    public const string DefaultQueueCredentialKey = "defaultQueueId";

    private readonly ITenantChannelConfigStore _channelConfigStore;
    private readonly IQueueStore _queueStore;

    public DefaultQueueFallbackMiddleware(
        ITenantChannelConfigStore channelConfigStore,
        IQueueStore queueStore)
    {
        _channelConfigStore = channelConfigStore;
        _queueStore = queueStore;
    }

    public override async Task<RouteResult?> RouteAsync(
        RoutingContext context,
        Func<Task<RouteResult?>> continuation,
        CancellationToken ct)
    {
        // Explicit upstream rules (ChannelQueueMapping, last-agent, etc.) win — only fall back when
        // nothing produced a queue.
        var downstream = await continuation().ConfigureAwait(false);
        if (downstream is not null)
        {
            return downstream;
        }

        var queueId = await ResolveDefaultQueueAsync(context.TenantId, context.Channel, ct).ConfigureAwait(false);
        return queueId is null
            ? null
            : new RouteResult(queueId.Value, MessagePriority.Normal, PreferredAgentId: null, Metadata: null);
    }

    private async Task<EntityId?> ResolveDefaultQueueAsync(TenantId tenantId, ChannelType channel, CancellationToken ct)
    {
        // Tier 1 — explicit per-channel default queue, validated as existing + active.
        var config = await _channelConfigStore.GetAsync(tenantId, channel, ct).ConfigureAwait(false);
        if (config?.Credentials is { } creds
            && creds.TryGetValue(DefaultQueueCredentialKey, out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            var configured = EntityId.From(raw);
            var queue = await _queueStore.GetByIdAsync(tenantId, configured, ct).ConfigureAwait(false);
            if (queue is { IsActive: true })
            {
                return configured;
            }
            // Stale / inactive configured queue → fall through to Tier 2.
        }

        // Tier 2 — the tenant's first active queue.
        var queues = await _queueStore.ListAsync(tenantId, new PagedQuery(Page: 1, PageSize: 50), ct).ConfigureAwait(false);
        foreach (var q in queues.Items)
        {
            if (q.IsActive)
            {
                return q.QueueId;
            }
        }

        return null;
    }
}
