using Asterisk.Platform.Api.Middleware;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Licensing;

namespace Asterisk.Platform.Api.Endpoints;

internal static class CallAttemptEndpoints
{
    public static void MapCallAttemptEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/call-attempts")
            .RequireAuthorization("AdminOnly")
            .WithTags("CallAttempts")
            .RequireLicenseFeature(LicenseFeature.Dialer);
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
