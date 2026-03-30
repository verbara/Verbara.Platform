using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

// ─── Settings store (unchanged, moved from SystemEndpoints.cs) ────────────────

internal sealed class SystemSettingsStore
{
    private readonly ConcurrentDictionary<string, SystemSettingsRecord> _settings = new();

    public SystemSettingsRecord Get() =>
        _settings.GetOrAdd("__global__", _ => new SystemSettingsRecord("Asterisk Platform", "UTC", "en-US"));

    public void Save(SystemSettingsRecord record) =>
        _settings["__global__"] = record;
}

internal sealed record SystemSettingsRecord(string PlatformName, string DefaultTimezone, string DefaultLanguage);

// ─── Endpoints ────────────────────────────────────────────────────────────────

internal static class ManagementSystemEndpoints
{
    public static void MapManagementSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/system").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/info", GetSystemInfo);
        group.MapGet("/license", GetLicenseInfo);
        group.MapPut("/license", UpdateLicense);
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", SaveSettings);
    }

    private static async Task<IResult> GetSystemInfo(
        [FromServices] IFeatureRegistry features,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var hostTenant = await tenantStore.GetHostTenantAsync(ct);
        return Results.Ok(new
        {
            version = "1.1.0",
            hostTenantId = hostTenant?.TenantId,
            platformName = hostTenant?.Name ?? "Asterisk Platform",
            features = features.GetFeatures(),
        });
    }

    private static IResult GetLicenseInfo()
    {
        return Results.Ok(new
        {
            tier = "community",
            features = Array.Empty<string>(),
            maxAgents = 10,
        });
    }

    private static IResult UpdateLicense([FromBody] UpdateLicenseRequest body)
    {
        // License activation will be implemented when Pro.Licensing supports runtime activation
        return Results.Ok(new
        {
            tier = "community",
            features = Array.Empty<string>(),
            message = "License activation not yet implemented.",
        });
    }

    private static IResult GetSettings([FromServices] SystemSettingsStore store)
    {
        var record = store.Get();
        return Results.Ok(new
        {
            platformName = record.PlatformName,
            defaultTimezone = record.DefaultTimezone,
            defaultLanguage = record.DefaultLanguage,
        });
    }

    private static IResult SaveSettings(
        [FromBody] SystemSettingsRequest body,
        [FromServices] SystemSettingsStore store)
    {
        var record = new SystemSettingsRecord(body.PlatformName, body.DefaultTimezone, body.DefaultLanguage);
        store.Save(record);
        return Results.Ok(new
        {
            platformName = record.PlatformName,
            defaultTimezone = record.DefaultTimezone,
            defaultLanguage = record.DefaultLanguage,
        });
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record UpdateLicenseRequest(string LicenseKey);
internal sealed record SystemSettingsRequest(string PlatformName, string DefaultTimezone, string DefaultLanguage);
