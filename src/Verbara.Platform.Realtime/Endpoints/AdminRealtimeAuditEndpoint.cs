using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Verbara.Platform.Realtime.Contracts;
using Verbara.Platform.Realtime.Services;

namespace Verbara.Platform.Realtime.Endpoints;

/// <summary>
/// <c>GET /admin/realtime/audit?since=&amp;limit=</c> — surfaces the
/// in-memory <see cref="IRelayOutcomeSink"/> ring buffer so E2E harnesses +
/// on-call operators can introspect what <c>PushToHubRelay</c> actually did
/// with the events it observed (Forwarded / SkippedNotLeader / SkippedNullTenant
/// / SkippedNullNodeId / ForwardError) on the responding pod.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> per-pod. A K8s Service routes the request to ONE replica;
/// callers iterating across pods MUST address each pod by name (e.g. via
/// <c>kubectl port-forward</c> in the harness's TalosTopologyProvider) and
/// aggregate client-side.
/// </para>
/// <para>
/// <b>Auth:</b> the <c>PlatformAdmin</c> policy is registered in
/// <c>Program.cs</c> alongside Supervisor/Agent. Production-grade tenants
/// MUST not surface this endpoint to non-admin traffic — the payload reveals
/// per-tenant event throughput patterns that would leak under cross-tenant
/// inspection.
/// </para>
/// </remarks>
public static class AdminRealtimeAuditEndpoint
{
    public static void MapAdminRealtimeAuditEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/realtime/audit", (
            [FromServices] IRelayOutcomeSink sink,
            [FromQuery] string? since,
            [FromQuery] int? limit) =>
        {
            DateTimeOffset? sinceTs = null;
            if (!string.IsNullOrEmpty(since))
            {
                if (!DateTimeOffset.TryParse(
                        since,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    return Results.Problem(
                        detail: "Invalid 'since' query parameter: expected ISO8601 UTC timestamp (e.g. 2026-05-24T05:30:00Z).",
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Bad Request");
                }
                sinceTs = parsed;
            }

            var requestedLimit = limit ?? 1_000;
            if (requestedLimit < 0)
            {
                return Results.Problem(
                    detail: "Invalid 'limit' query parameter: must be >= 0.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Bad Request");
            }

            var page = sink.Snapshot(sinceTs, requestedLimit);
            return Results.Json(page, RealtimeContractsJsonContext.Default.RelayOutcomePage);
        })
        .RequireAuthorization("PlatformAdmin")
        .WithName("AdminRealtimeAudit");
    }
}
