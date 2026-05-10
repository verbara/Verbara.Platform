using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Closes <c>PREPUB-2026-05-09-MT-001</c>: any tenant Admin holding a valid
/// JWT could scope their request to a foreign Customer tenant by appending
/// an <c>X-Tenant-Id: victim</c> header.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TenantResolutionMiddleware"/> resolves the header BEFORE
/// <c>UseAuthentication</c> runs. The JWT bearer handler's
/// <c>OnTokenValidated</c> only sets the tenant if <c>Items["TenantId"]</c>
/// is empty. Result: the bare <c>AdminOnly</c> surfaces
/// (<c>/admin/users|queues|agents|teams|audit</c>) read
/// <c>Items["TenantId"]</c> unconditionally and return another tenant's data
/// to any authenticated Customer-tenant Admin.
/// </para>
/// <para>
/// This middleware runs immediately after <c>UseAuthorization</c> and
/// rejects with <c>403 Forbidden</c> when the principal's <c>tid</c> /
/// <c>tenant_id</c> claim does not match the resolved
/// <c>Items["TenantId"]</c>. It permits two legitimate cross-tenant patterns:
/// <list type="bullet">
///   <item><description><b>Management API key</b>
///   (<c>key_type=management</c>) — the bypass is intentional; scope-aware
///   enforcement is tracked separately as <c>PREPUB-2026-05-09-ADMIN-002</c>.</description></item>
///   <item><description><b>Platform / Partner tenants</b> — these manage
///   downstream tenants by design (R5.2 PA, ADR-0002).</description></item>
/// </list>
/// </para>
/// <para>
/// The <see cref="ITenantStore"/> is resolved per-request via
/// <see cref="HttpContext.RequestServices"/> rather than constructor-injected
/// so the middleware itself stays cheap to instantiate (singleton lifetime
/// with no captured store reference). The store lookup only fires when the
/// header was actually overridden (small fraction of requests in steady
/// state) and is hot-cached behind <c>CachedTenantStore</c> in production.
/// </para>
/// </remarks>
internal sealed class TenantBoundaryValidationMiddleware
{
    private readonly RequestDelegate _next;

    public TenantBoundaryValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Anonymous requests: nothing to enforce; downstream auth gates handle them.
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        // Management API keys are intentionally exempt (ADMIN-002 follow-up).
        if (string.Equals(context.User.FindFirst("key_type")?.Value, "management", StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        var jwtTid = context.User.FindFirst("tid")?.Value
                     ?? context.User.FindFirst("tenant_id")?.Value;

        // No principal-tenant claim or no resolved tenant: nothing to compare.
        if (jwtTid is null || !context.Items.TryGetValue("TenantId", out var resolvedRaw)
            || resolvedRaw is not TenantId resolved)
        {
            await _next(context);
            return;
        }

        // Header / subdomain matches principal: nothing to enforce.
        if (string.Equals(jwtTid, resolved.Value, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Mismatch: only Platform / Partner callers may legitimately operate on
        // a different tenant via header / subdomain.
        var store = context.RequestServices.GetRequiredService<ITenantStore>();
        var callerTenant = await store.GetAsync(jwtTid, context.RequestAborted);
        if (callerTenant?.Type is TenantType.Platform or TenantType.Partner)
        {
            await _next(context);
            return;
        }

        await WriteForbiddenAsync(context);
    }

    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        var error = new ErrorResponse("Tenant header does not match authenticated principal.");
        await JsonSerializer.SerializeAsync(
            context.Response.Body, error, ApiJsonContext.Default.ErrorResponse, context.RequestAborted);
    }
}
