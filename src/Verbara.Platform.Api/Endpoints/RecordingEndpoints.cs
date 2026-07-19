using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.EventStore;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Endpoints;

internal static class RecordingEndpoints
{
    public static void MapRecordingEndpoints(this IEndpointRouteBuilder app)
    {
        var recordings = app.MapGroup("/recordings")
            .RequireAuthorization("SupervisorPlus")
            .RequireOperationalTenant()
            .RequirePlanFeature(PlanFeature.Recordings);

        recordings.MapGet("/{sessionId}", GetRecordingMetadata);
        recordings.MapGet("/{sessionId}/stream", StreamRecording);
    }

    // ─── Handlers ─────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<RecordingMetadataDto>, NotFound>> GetRecordingMetadata(
        string sessionId,
        HttpContext context,
        [FromServices] ICompletedSessionStore cdrStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var row = await cdrStore.GetAsync(tenantId, sessionId, ct);

        if (row is null || string.IsNullOrWhiteSpace(row.RecordingName))
            return TypedResults.NotFound();

        return TypedResults.Ok(new RecordingMetadataDto(
            SessionId: row.SessionId,
            RecordingName: row.RecordingName,
            HasRecording: true,
            StreamUrl: $"/api/recordings/{row.SessionId}/stream"));
    }

    private static async Task<IResult> StreamRecording(
        string sessionId,
        HttpContext context,
        [FromServices] ICompletedSessionStore cdrStore,
        IOptions<RecordingOptions> options,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var row = await cdrStore.GetAsync(tenantId, sessionId, ct);

        if (row is null || string.IsNullOrWhiteSpace(row.RecordingName))
            return Results.NotFound();

        var recordingName = row.RecordingName;
        string[] extensions = [".wav", ".gsm", ".ogg", ""];
        string? filePath = null;

        foreach (var ext in extensions)
        {
            filePath = ResolveRecordingPath(options.Value.BasePath, tenantId, recordingName, ext);
            if (filePath is not null) break;
        }

        if (filePath is null)
            return Results.NotFound();

        var contentType = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".gsm" => "audio/x-gsm",
            ".ogg" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream",
        };

        context.Response.Headers.CacheControl = "private, max-age=86400";

        await audit.RecordAsync(
            new TenantId(tenantId), category: "data_access", action: "recording.played", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: sessionId, targetType: "recording",
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
                ["recording_name"] = recordingName,
            },
            ct: ct);

        return Results.File(filePath, contentType: contentType, enableRangeProcessing: true);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string? ResolveRecordingPath(string basePath, string tenantId, string recordingName, string ext)
    {
        var safeName = Path.GetFileName(recordingName);
        if (string.IsNullOrEmpty(safeName)) return null;

        // Try tenant-isolated path first
        var tenantDir = Path.GetFullPath(Path.Combine(basePath, tenantId));
        var tenantPath = Path.GetFullPath(Path.Combine(tenantDir, safeName + ext));
        if (File.Exists(tenantPath) && tenantPath.StartsWith(tenantDir, StringComparison.Ordinal))
            return tenantPath;

        // Fallback to legacy flat structure (bounds-checked)
        var baseDir = Path.GetFullPath(basePath);
        var legacyPath = Path.GetFullPath(Path.Combine(baseDir, safeName + ext));
        if (File.Exists(legacyPath) && legacyPath.StartsWith(baseDir, StringComparison.Ordinal))
            return legacyPath;

        return null;
    }

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Options ──────────────────────────────────────────────────────────────────

/// <summary>
/// Configuration for recording file storage location.
/// </summary>
public sealed class RecordingOptions
{
    /// <summary>
    /// Base directory where Asterisk stores recording files.
    /// Defaults to the standard Asterisk recording spool directory.
    /// </summary>
    public string BasePath { get; set; } = "/var/spool/asterisk/recording";
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record RecordingMetadataDto(
    string SessionId,
    string RecordingName,
    bool HasRecording,
    string StreamUrl);
