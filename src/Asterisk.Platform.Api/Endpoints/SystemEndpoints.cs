using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Endpoints;

internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/system").RequireAuthorization("AdminOnly");

        group.MapGet("/info", GetSystemInfo);
        group.MapGet("/license", GetLicenseInfo);
        group.MapGet("/cluster", GetClusterStatus);
    }

    private static IResult GetSystemInfo(HttpContext context, IFeatureRegistry features)
    {
        var tenantId = GetTenantId(context);
        return Results.Ok(new
        {
            version = "1.0.0",
            tenantId = tenantId.ToString(),
            features = features.GetFeatures(),
        });
    }

    private static IResult GetLicenseInfo(IServiceProvider services)
    {
        // Pro.Licensing may not be registered — return community defaults
        return Results.Ok(new
        {
            tier = "community",
            features = Array.Empty<string>(),
            maxAgents = 10,
        });
    }

    private static IResult GetClusterStatus(IServiceProvider services)
    {
        // Pro.Cluster may not be registered — return single-node default
        return Results.Ok(new
        {
            nodes = new[]
            {
                new { id = "local", status = "healthy" },
            },
        });
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}
