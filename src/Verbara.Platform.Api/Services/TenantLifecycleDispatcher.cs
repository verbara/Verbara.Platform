using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// <see cref="ITenantLifecycleDispatcher"/> (realtime-objects-stay-in-their-tenant, design D3): an unflagged tenant
/// reaches every registered handler; a flagged one, or one whose audit cannot be read, gets
/// <see cref="FlaggedTenantRealtimeRemoval"/> and then only <see cref="IPlatformTenantLifecycleHandler"/>s, so no
/// handler outside Platform (Pro's realtime cleanup, which sweeps queue rows by prefix) runs for it. The audit is read
/// once per dispatch; flags never change on their own, so that read is current when the removal runs.
/// </summary>
internal sealed partial class TenantLifecycleDispatcher : ITenantLifecycleDispatcher
{
    private const string UnreadableAudit = "the tenant-id audit could not be read";

    private readonly IEnumerable<ITenantLifecycleHandler> _handlers;
    private readonly TenantIdAudit _audit;
    private readonly FlaggedTenantRealtimeRemoval _removal;
    private readonly ILogger<TenantLifecycleDispatcher> _logger;

    public TenantLifecycleDispatcher(
        IEnumerable<ITenantLifecycleHandler> handlers,
        TenantIdAudit audit,
        FlaggedTenantRealtimeRemoval removal,
        ILogger<TenantLifecycleDispatcher> logger)
    {
        _handlers = handlers;
        _audit = audit;
        _removal = removal;
        _logger = logger;
    }

    public async ValueTask CreatedAsync(Tenant tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        foreach (var handler in _handlers)
            await RunAsync(handler, tenant.TenantId, h => h.OnTenantCreatedAsync(tenant, ct)).ConfigureAwait(false);
    }

    public ValueTask SuspendedAsync(string tenantId, CancellationToken ct) =>
        DispatchAsync(tenantId, (h, token) => h.OnTenantSuspendedAsync(tenantId, token), ct);

    public ValueTask DeletedAsync(string tenantId, CancellationToken ct) =>
        DispatchAsync(tenantId, (h, token) => h.OnTenantDeletedAsync(tenantId, token), ct);

    private async ValueTask DispatchAsync(string tenantId, Func<ITenantLifecycleHandler, CancellationToken, ValueTask> action,
        CancellationToken ct)
    {
        string? flag;
        try
        {
            flag = (await _audit.RunAsync(ct).ConfigureAwait(false)).ReasonOf(tenantId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuditFailed(_logger, tenantId, ex);
            flag = UnreadableAudit;
        }

        if (flag is null)
        {
            foreach (var handler in _handlers)
                await RunAsync(handler, tenantId, h => action(h, ct)).ConfigureAwait(false);
            return;
        }

        LogFlaggedDispatch(_logger, tenantId, flag);
        await _removal.RemoveAsync(tenantId, flag, CancellationToken.None).ConfigureAwait(false);
        foreach (var handler in _handlers.OfType<IPlatformTenantLifecycleHandler>())
            await RunAsync(handler, tenantId, h => action(h, CancellationToken.None)).ConfigureAwait(false);
    }

    private async ValueTask RunAsync(ITenantLifecycleHandler handler, string tenantId, Func<ITenantLifecycleHandler, ValueTask> action)
    {
        try
        {
            await action(handler).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogHandlerFailed(_logger, handler.GetType().Name, tenantId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lifecycle handler {Handler} failed for tenant {TenantId}")]
    private static partial void LogHandlerFailed(ILogger logger, string handler, string tenantId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "The tenant-id audit could not be read for tenant {TenantId}; it is treated as flagged")]
    private static partial void LogAuditFailed(ILogger logger, string tenantId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {TenantId} is flagged ({Reason}): its realtime rows are removed by exact id and only Platform's lifecycle handlers run")]
    private static partial void LogFlaggedDispatch(ILogger logger, string tenantId, string reason);
}
