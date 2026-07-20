using Microsoft.Extensions.Logging;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Models;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// ADR-0012 Ola-3 — best-effort Asterisk-realtime-sync decorator over <see cref="IQueueStore"/>.
/// Migrates the queue sync that previously lived as a <c>RequestServices.GetService&lt;IRealtimeSyncService&gt;()</c>
/// Service-Locator resolve inside <c>AdminEndpoints</c> (Create/Update/Delete queue) into the
/// storage seam. A <see cref="SaveAsync"/> upserts the Asterisk queue row; a <see cref="DeleteAsync"/>
/// removes it (resolving the queue name from the inner store BEFORE the delete). Every sync is
/// best-effort: a throw is swallowed + logged (EventId 4130) so the store write still succeeds —
/// the <see cref="RealtimeReconciliationService"/> re-converges any missed upsert.
/// </summary>
internal sealed class RealtimeSyncingQueueStore : IQueueStore
{
    private readonly IQueueStore _inner;
    private readonly IRealtimeSyncService _sync;
    private readonly ILogger<RealtimeSyncingQueueStore> _logger;

    public RealtimeSyncingQueueStore(
        IQueueStore inner,
        IRealtimeSyncService sync,
        ILogger<RealtimeSyncingQueueStore> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _sync = sync;
        _logger = logger;
    }

    public Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
        => _inner.GetByIdAsync(tenantId, queueId, ct);

    public Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
        => _inner.ListAsync(tenantId, query, ct);

    public async Task SaveAsync(Queue queue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queue);

        await _inner.SaveAsync(queue, ct).ConfigureAwait(false);

        try
        {
            // Mirror AdminEndpoints' RealtimeQueueOptions construction (was :295-301).
            var opts = new RealtimeQueueOptions
            {
                Timeout = 30,
                Wrapuptime = queue.WrapUp?.DefaultWrapUpSeconds ?? 15,
                Servicelevel = queue.SlaTargets?.AnswerWithinSeconds ?? 20,
                Maxlen = queue.MaxWaiting ?? 0,
            };
            await _sync.SyncQueueAsync(queue.TenantId.Value, queue.Name, opts, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RealtimeSyncDeferralLog.Deferred(_logger, "SyncQueue", queue.Name, queue.TenantId.Value, ex);
        }
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        // RemoveQueueAsync is keyed by queue NAME, not id — resolve it via the inner
        // store BEFORE the delete removes the row.
        var queue = await _inner.GetByIdAsync(tenantId, queueId, ct).ConfigureAwait(false);

        await _inner.DeleteAsync(tenantId, queueId, ct).ConfigureAwait(false);

        if (queue is null)
            return;

        try
        {
            await _sync.RemoveQueueAsync(tenantId.Value, queue.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RealtimeSyncDeferralLog.Deferred(_logger, "RemoveQueue", queue.Name, tenantId.Value, ex);
        }
    }
}
