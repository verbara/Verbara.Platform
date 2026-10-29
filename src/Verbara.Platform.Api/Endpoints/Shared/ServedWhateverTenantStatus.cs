namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Endpoint metadata marking a route that <see cref="Middleware.TenantStatusMiddleware"/> serves whatever the
/// status of the tenant the request names: the middleware calls the next middleware without refusing and without
/// populating its tenant caches (licensed-agent-metering-follow-ups, design D1). Read from the routed endpoint
/// through <c>GetMetadata&lt;T&gt;()</c>, never matched by path.
/// </summary>
/// <remarks>
/// Meant for exactly one route — <c>DELETE /management/impersonate</c>, which only ends the caller's own
/// impersonation — so that ending an impersonation never depends on the target tenant's status. An architecture
/// test holds the route table to that single endpoint.
/// </remarks>
internal sealed class ServedWhateverTenantStatusMetadata
{
    public static readonly ServedWhateverTenantStatusMetadata Instance = new();

    private ServedWhateverTenantStatusMetadata()
    {
    }
}

internal static class ServedWhateverTenantStatusEndpointExtensions
{
    /// <summary>
    /// Exempts this route from the tenant-status refusal: a suspended or pending-deletion tenant does not stop the
    /// request from reaching its handler. Applied only to <c>DELETE /management/impersonate</c> (design D1).
    /// </summary>
    public static RouteHandlerBuilder ServedWhateverTenantStatus(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(ServedWhateverTenantStatusMetadata.Instance);
        return builder;
    }
}
