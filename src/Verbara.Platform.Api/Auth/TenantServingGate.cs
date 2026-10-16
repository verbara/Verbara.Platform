using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Auth;

/// <summary>
/// The tenant-status rule of the sign-in handlers, for the tenant each one resolves on its own (login body,
/// API key, MFA challenge, refresh token, OIDC flow state). The mapping is the one
/// <see cref="Middleware.TenantStatusMiddleware"/> applies to a tenant named by header, subdomain or token:
/// Suspended and PendingDeletion answer 403 with their <see cref="TenantStatusRefusals"/> code, Deleted answers
/// 404, and every other status serves. A tenant the store does not know is not refused here, as in the
/// middleware: the user lookup that follows fails as it would anyway.
/// </summary>
internal static class TenantServingGate
{
    /// <summary>
    /// The status of <paramref name="tenantId"/> when it may not serve a sign-in, or <see langword="null"/>
    /// when it may (or the store does not know it).
    /// </summary>
    internal static async ValueTask<TenantStatus?> RefusedStatusAsync(
        ITenantStore tenants, string tenantId, CancellationToken ct)
    {
        var tenant = await tenants.GetAsync(tenantId, ct).ConfigureAwait(false);
        return tenant?.Status is TenantStatus.Suspended or TenantStatus.PendingDeletion or TenantStatus.Deleted
            ? tenant.Status
            : null;
    }

    /// <summary>
    /// The sign-in endpoints' answer for a refused tenant status: the same status code and body the
    /// middleware gives on a coded sign-in route.
    /// </summary>
    internal static IResult Refusal(TenantStatus status) => status switch
    {
        TenantStatus.Suspended => Error(StatusCodes.Status403Forbidden, TenantStatusRefusals.Suspended),
        TenantStatus.PendingDeletion => Error(StatusCodes.Status403Forbidden, TenantStatusRefusals.PendingDeletion),
        _ => Error(StatusCodes.Status404NotFound, "Not found"),
    };

    /// <summary>The auth-event reason recorded when a tenant status ends a session, e.g. <c>tenant_suspended</c>.</summary>
    internal static string DenialReason(TenantStatus status) => status switch
    {
        TenantStatus.Suspended => "tenant_suspended",
        TenantStatus.PendingDeletion => "tenant_pending_deletion",
        _ => "tenant_deleted",
    };

    /// <summary>
    /// Refuses the sign-in when <paramref name="tenantId"/> may not serve it: the response to return, or
    /// <see langword="null"/> to go on.
    /// </summary>
    internal static async ValueTask<IResult?> RefuseAsync(ITenantStore tenants, string tenantId, CancellationToken ct) =>
        await RefusedStatusAsync(tenants, tenantId, ct).ConfigureAwait(false) is { } status
            ? Refusal(status)
            : null;

    private static IResult Error(int statusCode, string error) =>
        Results.Json(new ErrorResponse(error), ApiJsonContext.Default.ErrorResponse, statusCode: statusCode);
}
