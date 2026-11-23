using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Core;

/// <summary>
/// The one place that runs the registered <see cref="ITenantLifecycleHandler"/>s for a tenant's creation, suspension or
/// deletion (realtime-objects-stay-in-their-tenant, design D3). A handler's failure is logged and the next one still
/// runs. A tenant whose stored id breaks the tenant-id rule reaches only Platform's own handlers
/// (<see cref="IPlatformTenantLifecycleHandler"/>) after its realtime rows are removed by exact id.
/// </summary>
public interface ITenantLifecycleDispatcher
{
    /// <summary>Runs every handler's <see cref="ITenantLifecycleHandler.OnTenantCreatedAsync"/>.</summary>
    ValueTask CreatedAsync(Tenant tenant, CancellationToken ct);

    /// <summary>Dispatches a tenant's suspension.</summary>
    ValueTask SuspendedAsync(string tenantId, CancellationToken ct);

    /// <summary>Dispatches a tenant's deletion.</summary>
    ValueTask DeletedAsync(string tenantId, CancellationToken ct);
}

/// <summary>
/// Marks a lifecycle handler that belongs to Platform. The dispatcher calls only these handlers for a tenant whose
/// stored id breaks the tenant-id rule; every other handler is called for every other tenant.
/// </summary>
#pragma warning disable CA1040 // a marker interface is the point: the dispatcher decides by it
public interface IPlatformTenantLifecycleHandler : ITenantLifecycleHandler
#pragma warning restore CA1040
{
}
