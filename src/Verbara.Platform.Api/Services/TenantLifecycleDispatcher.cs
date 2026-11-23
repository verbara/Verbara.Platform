using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// <see cref="ITenantLifecycleDispatcher"/> (realtime-objects-stay-in-their-tenant, design D3): an unflagged tenant
/// reaches every registered handler; a flagged one, or one whose audit cannot be read, gets
/// <see cref="FlaggedTenantRealtimeRemoval"/> and then only <see cref="IPlatformTenantLifecycleHandler"/>s.
/// </summary>
internal sealed class TenantLifecycleDispatcher : ITenantLifecycleDispatcher
{
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

    public ValueTask CreatedAsync(Tenant tenant, CancellationToken ct) => throw new NotImplementedException();

    public ValueTask SuspendedAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();

    public ValueTask DeletedAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
}
