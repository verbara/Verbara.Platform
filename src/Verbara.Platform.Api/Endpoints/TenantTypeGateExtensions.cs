using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// ADR-0027 — endpoint-filter extensions that enforce <see cref="TenantType"/>
/// invariants at the operational endpoint surface.
/// </summary>
/// <remarks>
/// The 3-tier tenant hierarchy (Platform / Partner / Customer) is structurally
/// enforced at the data layer (DB-unique platform tenant via partial index,
/// max depth 3, Partner-must-be-child-of-Platform) but operational endpoints
/// historically check only RBAC policies (AdminOnly / SupervisorPlus). This
/// filter closes the gap: routes whose semantics belong only inside a
/// <see cref="TenantType.Customer"/> tenant (agents, queues, conversations,
/// channels, campaigns, skills, bots, flows, etc.) reject Platform and
/// Partner callers with HTTP 409 + a structured remediation hint.
/// </remarks>
internal static class TenantTypeGateExtensions
{
    /// <summary>
    /// Rejects calls with HTTP 409 Conflict when the resolved tenant's
    /// <see cref="Tenant.Type"/> is not <see cref="TenantType.Customer"/>. Under impersonation the resolved
    /// tenant is already the impersonated Customer, so this filter passes
    /// naturally and no special case is needed.
    /// </summary>
    public static RouteGroupBuilder RequireOperationalTenant(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(static async (context, next) =>
        {
            var httpContext = context.HttpContext;

            // Defence in depth: TenantStatusMiddleware always populates
            // Items["Tenant"] for authenticated requests with a valid
            // tenant ID. If it's missing the pipeline upstream failed —
            // treat as unauthenticated rather than letting the operational
            // endpoint run on a null tenant.
            if (httpContext.Items["Tenant"] is not Tenant tenant)
            {
                return Results.Json(
                    new ErrorResponse("Tenant context could not be resolved."),
                    ApiJsonContext.Default.ErrorResponse,
                    statusCode: 401);
            }

            if (tenant.Type == TenantType.Customer)
                return await next(context);

            var problem = new TenantTypeMismatchProblem(
                Type: "https://verbara.platform/errors/tenant-type-mismatch",
                Title: "Operational endpoint not available on this tenant type",
                Status: 409,
                Detail: $"Operational endpoints are only available on Customer tenants " +
                        $"(this is a {tenant.Type} tenant). Use POST /api/v1/management/impersonate " +
                        $"{{\"tenantId\":\"<customer-id>\"}} to drive operational endpoints as that Customer.",
                TenantType: tenant.Type.ToString(),
                ExpectedType: nameof(TenantType.Customer));

            return Results.Json(problem, ApiJsonContext.Default.TenantTypeMismatchProblem, statusCode: 409);
        });

        return group;
    }
}

/// <summary>
/// RFC 7807 ProblemDetails-shaped error returned by
/// <see cref="TenantTypeGateExtensions.RequireOperationalTenant"/> when an
/// operational endpoint is invoked on a non-Customer tenant.
/// </summary>
/// <param name="Type">Stable error type URI for clients to switch on.</param>
/// <param name="Title">Short human-readable summary.</param>
/// <param name="Status">HTTP status code (always 409 for this problem).</param>
/// <param name="Detail">Operator-actionable explanation including remediation.</param>
/// <param name="TenantType">The caller tenant's type as the discriminator name.</param>
/// <param name="ExpectedType">Always <c>"Customer"</c>; explicit for clients that key on it.</param>
internal sealed record TenantTypeMismatchProblem(
    string Type,
    string Title,
    int Status,
    string Detail,
    string TenantType,
    string ExpectedType);
