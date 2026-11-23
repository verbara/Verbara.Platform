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
/// the <see cref="RealtimeReconciliationService"/> re-converges any missed upsert. A save also hands
/// the wrap-up to <see cref="QueueWrapUpDurationProvider"/> before the sync, so a failed sync still applies it.
/// </summary>
internal sealed class RealtimeSyncingQueueStore : IQueueStore
{
    private readonly IQueueStore _inner;
    private readonly IRealtimeSyncService _sync;
    private readonly QueueWrapUpDurationProvider _wrapUps;
    private readonly ILogger<RealtimeSyncingQueueStore> _logger;
    private readonly TenantIdAudit? _tenantIdAudit;

    // tenantIdAudit (realtime-objects-stay-in-their-tenant, design D2): read on each delete; a flagged tenant, or an
    // unreadable audit, keeps the realtime queue row. The composition always passes it; only a test that builds the
    // decorator by hand leaves it out.
    public RealtimeSyncingQueueStore(
        IQueueStore inner,
        IRealtimeSyncService sync,
        QueueWrapUpDurationProvider wrapUps,
        ILogger<RealtimeSyncingQueueStore> logger,
        TenantIdAudit? tenantIdAudit = null)
    {
        _tenantIdAudit = tenantIdAudit;
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(wrapUps);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _sync = sync;
        _wrapUps = wrapUps;
        _logger = logger;
    }

    public Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
        => _inner.GetByIdAsync(tenantId, queueId, ct);

    public Task<Queue?> GetByNameAsync(TenantId tenantId, string name, CancellationToken ct)
        => _inner.GetByNameAsync(tenantId, name, ct);

    public Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
        => _inner.ListAsync(tenantId, query, ct);

    public Task<IReadOnlyList<Queue>> ListPageAfterAsync(TenantId tenantId, EntityId? afterQueueId, int limit, CancellationToken ct)
        => _inner.ListPageAfterAsync(tenantId, afterQueueId, limit, ct);

    public async Task SaveAsync(Queue queue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queue);

        await _inner.SaveAsync(queue, ct).ConfigureAwait(false);

        _wrapUps.Update(
            QueueWrapUpDurationProvider.KeyOf(queue.TenantId, queue.Name),
            TimeSpan.FromSeconds(queue.WrapUp.DefaultWrapUpSeconds));

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

        // A queue row of a flagged tenant may be another tenant's row too (acme's `corp-x` is acme-corp's `x`), so it
        // is never removed for one; nor when the audit cannot be read (fail-closed). The flagged set never changes on
        // its own, so this read is current when the removal would run.
        if (_tenantIdAudit is not null)
        {
            string? flag;
            try
            {
                flag = (await _tenantIdAudit.RunAsync(ct).ConfigureAwait(false)).ReasonOf(tenantId.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RealtimeSyncDeferralLog.Deferred(_logger, "ReadTenantIdAudit", queue.Name, tenantId.Value, ex);
                flag = "the tenant-id audit could not be read";
            }
            if (flag is not null)
            {
                FlaggedTenantRealtimeRemoval.LogKeptQueue(_logger, $"{tenantId.Value}-{queue.Name}", tenantId.Value, flag);
                return;
            }
        }

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
