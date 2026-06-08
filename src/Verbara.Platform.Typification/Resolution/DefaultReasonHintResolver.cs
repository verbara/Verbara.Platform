using Verbara.Platform.Core;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Typification.Resolution;

/// <summary>
/// Default <see cref="IReasonHintResolver"/>: resolves an inbound context against
/// active <see cref="ReasonHint"/>s most-specific-wins in the order DID → Queue →
/// Channel, short-circuiting on the first scope that yields an active hint. Within a
/// scope the winner is the highest <see cref="ReasonHint.Priority"/>, tie-broken by
/// <see cref="ReasonHint.HintId"/> ordinal for determinism. A more-specific scope that
/// produces no active hint falls through to the next (it is "most-specific that
/// matches", not "most-specific provided").
/// </summary>
public sealed class DefaultReasonHintResolver : IReasonHintResolver
{
    private readonly IReasonHintStore _hintStore;

    public DefaultReasonHintResolver(IReasonHintStore hintStore)
    {
        _hintStore = hintStore;
    }

    public async Task<ReasonHint?> ResolveAsync(
        TenantId tenantId,
        string? did,
        EntityId? queueId,
        ChannelType channel,
        CancellationToken ct)
    {
        // Precedence (most-specific first): DID → Queue → Channel. A scope is queried
        // only when its context value is present; if it yields no active hint, fall
        // through to the next scope.
        if (!string.IsNullOrEmpty(did))
        {
            var byDid = await ResolveScopeAsync(tenantId, ReasonHintScope.Did, did, ct).ConfigureAwait(false);
            if (byDid is not null)
                return byDid;
        }

        if (queueId is { } queue)
        {
            var byQueue = await ResolveScopeAsync(tenantId, ReasonHintScope.Queue, queue.Value, ct).ConfigureAwait(false);
            if (byQueue is not null)
                return byQueue;
        }

        // Channel is always available (enum NAME, e.g. "Voice", is the ScopeRef convention).
        return await ResolveScopeAsync(tenantId, ReasonHintScope.Channel, channel.ToString(), ct).ConfigureAwait(false);
    }

    private async Task<ReasonHint?> ResolveScopeAsync(
        TenantId tenantId,
        ReasonHintScope scope,
        string scopeRef,
        CancellationToken ct)
    {
        var hints = await _hintStore.ListByScopeAsync(tenantId, scope, scopeRef, ct).ConfigureAwait(false);
        if (hints.Count == 0)
            return null;

        // Highest Priority first; tie-break by HintId ordinal for determinism.
        return hints
            .Where(static h => h.IsActive)
            .OrderByDescending(static h => h.Priority)
            .ThenBy(static h => h.HintId.Value, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
