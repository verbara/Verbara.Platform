using System.Collections.Concurrent;
using System.Reflection;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Licensing;
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
        var group = app.MapGroup("/management/system").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/info", GetSystemInfo);
        group.MapGet("/license", GetLicenseInfo);
        group.MapPut("/license", UpdateLicense);
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", SaveSettings);
    }

    private static async Task<IResult> GetSystemInfo(
        [FromServices] Asterisk.Platform.Core.IFeatureRegistry features,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var hostTenant = await tenantStore.GetHostTenantAsync(ct);
        var version = typeof(ManagementSystemEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return Results.Ok(new SystemInfoDto(version, hostTenant?.TenantId, hostTenant?.Name ?? "Asterisk Platform", features.GetFeatures()));
    }

    private static IResult GetLicenseInfo([FromServices] ILicenseStatus licenseStatus)
    {
        var features = new List<string>();
        foreach (var feature in Enum.GetValues<LicenseFeature>())
        {
            if (feature != LicenseFeature.None && feature != LicenseFeature.All
                && licenseStatus.LicensedFeatures.HasFlag(feature))
            {
                features.Add(feature.ToString());
            }
        }

        return Results.Ok(new LicenseInfoDto(
            licenseStatus.IsValid,
            licenseStatus.LicenseId,
            licenseStatus.Licensee,
            licenseStatus.LastResult.ToString(),
            licenseStatus.ExpiresAt,
            features,
            licenseStatus.MaxNodes,
            licenseStatus.LastValidatedAt));
    }

    private static IResult UpdateLicense(
        [FromBody] UpdateLicenseRequest body,
        [FromServices] ILicenseStatus licenseStatus)
    {
        return Results.Ok(new MessageResponse("License activation not yet implemented. Place a .lic file at the configured path and restart."));
    }

    private static IResult GetSettings([FromServices] SystemSettingsStore store)
    {
        var record = store.Get();
        return Results.Ok(new SystemSettingsDto(record.PlatformName, record.DefaultTimezone, record.DefaultLanguage));
    }

    private static async Task<IResult> SaveSettings(
        HttpContext context,
        [FromBody] SystemSettingsRequest body,
        [FromServices] SystemSettingsStore store,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var before = store.Get();
        var record = new SystemSettingsRecord(body.PlatformName, body.DefaultTimezone, body.DefaultLanguage);
        store.Save(record);

        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is not null)
        {
            await audit.RecordAsync(
                new TenantId(host.TenantId), category: "admin", action: "system.configured", severity: "critical",
                actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
                targetId: host.TenantId, targetType: "system",
                changes: new AuditChanges(Before: new { before.PlatformName, before.DefaultTimezone, before.DefaultLanguage }, After: new { record.PlatformName, record.DefaultTimezone, record.DefaultLanguage }),
                metadata: new Dictionary<string, string>
                {
                    ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    ["endpoint"] = context.Request.Path.Value ?? "",
                },
                ct: ct);
        }

        return Results.Ok(new SystemSettingsDto(record.PlatformName, record.DefaultTimezone, record.DefaultLanguage));
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record UpdateLicenseRequest(string LicenseKey);
internal sealed record SystemSettingsRequest(string PlatformName, string DefaultTimezone, string DefaultLanguage);

internal sealed record SystemInfoDto(string Version, string? HostTenantId, string? PlatformName, IReadOnlyDictionary<string, bool> Features);
internal sealed record LicenseInfoDto(
    bool IsValid,
    string? LicenseId,
    string? Licensee,
    string Status,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> LicensedFeatures,
    int MaxNodes,
    DateTimeOffset LastValidatedAt);
internal sealed record SystemSettingsDto(string PlatformName, string DefaultTimezone, string DefaultLanguage);
