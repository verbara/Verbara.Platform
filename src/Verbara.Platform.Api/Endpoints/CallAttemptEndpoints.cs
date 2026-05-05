using Verbara.Platform.Api.Middleware;
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
            .WithTags("CallAttempts")
            .RequireLicenseFeature(LicenseFeature.Dialer)
            .RequirePlanFeature(PlanFeature.Dialer);
        group.MapPut("/{id:long}/disposition", UpdateDisposition);
    }

    private static async Task<IResult> UpdateDisposition(
        long id, UpdateCallAttemptDispositionRequest request,
        HttpContext context, CampaignStoreBase store, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.UpdateCallAttemptDispositionAsync(tenantId, id, request.DispositionId, request.AgentComment, ct);
        return Results.NoContent();
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
