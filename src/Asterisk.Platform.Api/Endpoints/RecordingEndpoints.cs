using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.EventStore;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Endpoints;

internal static class RecordingEndpoints
{
    public static void MapRecordingEndpoints(this IEndpointRouteBuilder app)
    {
        var recordings = app.MapGroup("/api/recordings").RequireAuthorization();

        recordings.MapGet("/{sessionId}", GetRecordingMetadata);
        recordings.MapGet("/{sessionId}/stream", StreamRecording);
    }

    // ─── Handlers ─────────────────────────────────────────────────────────────

    private static async Task<IResult> GetRecordingMetadata(
        string sessionId,
        HttpContext context,
        ICompletedSessionStore cdrStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var row = await cdrStore.GetAsync(tenantId, sessionId, ct);

        if (row is null || string.IsNullOrWhiteSpace(row.RecordingName))
            return Results.NotFound();

        return Results.Ok(new RecordingMetadataDto(
            SessionId: row.SessionId,
            RecordingName: row.RecordingName,
            HasRecording: true,
            StreamUrl: $"/api/recordings/{row.SessionId}/stream"));
    }

    private static async Task<IResult> StreamRecording(
        string sessionId,
        HttpContext context,
        ICompletedSessionStore cdrStore,
        IOptions<RecordingOptions> options,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var row = await cdrStore.GetAsync(tenantId, sessionId, ct);

        if (row is null || string.IsNullOrWhiteSpace(row.RecordingName))
            return Results.NotFound();

        var recordingName = row.RecordingName;

        // Try common extensions
        string[] extensions = [".wav", ".gsm", ".ogg", ""];
        string? filePath = null;

        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(options.Value.BasePath, recordingName + ext);
            if (File.Exists(candidate))
            {
                filePath = candidate;
                break;
            }
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

        return Results.File(filePath, contentType: contentType, enableRangeProcessing: true);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

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
