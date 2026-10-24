using Verbara.Platform.Api.Middleware;
using Microsoft.AspNetCore.Http.HttpResults;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Endpoints;

internal static class CallAttemptEndpoints
{
    public static void MapCallAttemptEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/call-attempts")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant()
            .WithTags("CallAttempts")
            .RequireLicenseFeature(LicenseFeature.Dialer)
            .RequirePlanFeature(PlanFeature.Dialer);
        group.MapPut("/{id:long}/disposition", UpdateDisposition);
    }

    /// <summary>Sets the disposition of call attempt <paramref name="id"/>; 404 when it is no attempt of the caller's tenant.</summary>
    private static async Task<Results<NoContent, NotFound>> UpdateDisposition(
        long id, UpdateCallAttemptDispositionRequest request,
        HttpContext context, CampaignStoreBase store, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        return await store.TryUpdateCallAttemptDispositionAsync(tenantId, id, request.DispositionId, request.AgentComment, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ──────────────────────────────────────────────────────────────

internal sealed record UpdateCallAttemptDispositionRequest(long DispositionId, string? AgentComment);
