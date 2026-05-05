using System.Collections.Concurrent;
using System.Reflection;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Endpoints;

// ─── Settings store (unchanged, moved from SystemEndpoints.cs) ────────────────

internal sealed class SystemSettingsStore
{
    private readonly ConcurrentDictionary<string, SystemSettingsRecord> _settings = new();

    public SystemSettingsRecord Get() =>
        _settings.GetOrAdd("__global__", _ => new SystemSettingsRecord("Verbara", "UTC", "en-US"));

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
        [FromServices] Verbara.Platform.Core.IFeatureRegistry features,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var hostTenant = await tenantStore.GetHostTenantAsync(ct);
        var version = typeof(ManagementSystemEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return Results.Ok(new SystemInfoDto(version, hostTenant?.TenantId, hostTenant?.Name ?? "Verbara", features.GetFeatures()));
    }

    private static IResult GetLicenseInfo(
        [FromServices] ILicenseStatus licenseStatus,
        [FromServices] IOptions<LicenseOptions> licenseOptions,
        [FromServices] TimeProvider timeProvider)
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

        // R5.2 PC.4 / triage limitation #11 — surface grace period state so the
        // Web admin StatCard can render real grace-window context. Logic mirrors
        // Verbara.Sdk.Pro.Licensing.LicenseValidator.Validate exactly:
        //   GracePeriod result   ⇒ in grace, remaining = ExpiresAt + grace - now
        //   Expired/Invalid      ⇒ blocked (grace is exhausted or never granted)
        //   MissingFeature       ⇒ blocked (license loaded but feature bit absent)
        //   Valid                ⇒ neither in grace nor blocked
        var (inGrace, gracePeriodRemaining, blocked) = ComputeGraceState(
            licenseStatus,
            licenseOptions.Value.GracePeriod,
            timeProvider.GetUtcNow());

        return Results.Ok(new LicenseInfoDto(
            licenseStatus.IsValid,
            licenseStatus.LicenseId,
            licenseStatus.Licensee,
            licenseStatus.LastResult.ToString(),
            licenseStatus.ExpiresAt,
            features,
            licenseStatus.MaxNodes,
            licenseStatus.LastValidatedAt,
            inGrace,
            gracePeriodRemaining,
            blocked));
    }

    // Internal so Endpoints unit tests can exercise the mapping without spinning
    // a full WebApplicationFactory. Pure function — no DI dependency.
    internal static (bool InGrace, TimeSpan? GracePeriodRemaining, bool Blocked) ComputeGraceState(
        ILicenseStatus status,
        TimeSpan gracePeriod,
        DateTimeOffset now)
    {
        switch (status.LastResult)
        {
            case LicenseValidationResult.GracePeriod when status.ExpiresAt is { } expiresAt:
            {
                var remaining = (expiresAt + gracePeriod) - now;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                return (InGrace: true, GracePeriodRemaining: remaining, Blocked: false);
            }
            case LicenseValidationResult.GracePeriod:
                // No expiry timestamp loaded (defensive — should not occur in
                // practice because the tracker only emits GracePeriod alongside
                // a key with ExpiresAt populated).
                return (InGrace: true, GracePeriodRemaining: null, Blocked: false);
            case LicenseValidationResult.Expired
                or LicenseValidationResult.Invalid
                or LicenseValidationResult.MissingFeature:
                return (InGrace: false, GracePeriodRemaining: null, Blocked: true);
            case LicenseValidationResult.Valid:
            default:
                return (InGrace: false, GracePeriodRemaining: null, Blocked: false);
        }
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
// PC.4 / triage limitation #11 (R5.2): adds InGrace / GracePeriodRemaining / Blocked
// at the end of the positional record so the wire shape is purely additive — existing
// JSON consumers ignore unknown fields, and TS clients pick up the new fields when
// the type definition is regenerated. See ComputeGraceState for the source-of-truth
// mapping rules.
internal sealed record LicenseInfoDto(
    bool IsValid,
    string? LicenseId,
    string? Licensee,
    string Status,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> LicensedFeatures,
    int MaxNodes,
    DateTimeOffset LastValidatedAt,
    bool InGrace,
    TimeSpan? GracePeriodRemaining,
    bool Blocked);
internal sealed record SystemSettingsDto(string PlatformName, string DefaultTimezone, string DefaultLanguage);
