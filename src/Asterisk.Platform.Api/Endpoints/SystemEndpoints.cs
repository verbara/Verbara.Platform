using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

// ─── In-memory singleton store for system settings ────────────────────────────

internal sealed class SystemSettingsStore
{
    private readonly ConcurrentDictionary<string, SystemSettingsRecord> _settings = new();

    public SystemSettingsRecord Get(string tenantId) =>
        _settings.GetOrAdd(tenantId, _ => new SystemSettingsRecord("Asterisk Platform", "UTC", "en-US"));

    public void Save(string tenantId, SystemSettingsRecord record) =>
        _settings[tenantId] = record;
}

internal sealed record SystemSettingsRecord(string PlatformName, string DefaultTimezone, string DefaultLanguage);

// ─── Endpoint registration ────────────────────────────────────────────────────

internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/system").RequireAuthorization("AdminOnly");

        group.MapGet("/info", GetSystemInfo);
        group.MapGet("/license", GetLicenseInfo);
        group.MapGet("/cluster", GetClusterStatus);
        group.MapGet("/settings", GetSettings);
        group.MapPost("/settings", SaveSettings);
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

    private static IResult GetSettings(HttpContext context, [FromServices] SystemSettingsStore store)
    {
        var tenantId = GetTenantId(context).ToString();
        var record = store.Get(tenantId);
        return Results.Ok(new
        {
            platformName = record.PlatformName,
            defaultTimezone = record.DefaultTimezone,
            defaultLanguage = record.DefaultLanguage,
        });
    }

    private static IResult SaveSettings(
        HttpContext context,
        [FromBody] SystemSettingsRequest body,
        [FromServices] SystemSettingsStore store)
    {
        var tenantId = GetTenantId(context).ToString();
        var record = new SystemSettingsRecord(body.PlatformName, body.DefaultTimezone, body.DefaultLanguage);
        store.Save(tenantId, record);
        return Results.Ok(new
        {
            platformName = record.PlatformName,
            defaultTimezone = record.DefaultTimezone,
            defaultLanguage = record.DefaultLanguage,
        });
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTO ──────────────────────────────────────────────────────────────

internal sealed record SystemSettingsRequest(string PlatformName, string DefaultTimezone, string DefaultLanguage);
