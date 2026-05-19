using Verbara.Platform.Api.Authz;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Realtime.Contracts.Dtos;
using Microsoft.AspNetCore.Http;

namespace Verbara.Platform.Api.Endpoints.Internal;

/// <summary>
/// Service-to-service endpoints consumed by Verbara.Platform.Realtime over
/// HTTP+JSON (X-Service-Key gated). Surfaces the two facades the Platform
/// Hub used to consume in-process (pre-ADR-0022): cached agent→tenant
/// resolution and the hub audit sink.
/// </summary>
internal static class InternalIntegrationEndpoints
{
    private const string ServiceKeyHeader = "X-Service-Key";

    public static void MapInternalIntegrationEndpoints(this IEndpointRouteBuilder app, string? expectedServiceKey)
    {
        var group = app.MapGroup("/api/v1/internal")
            .AllowAnonymous()
            .AddEndpointFilter(async (ctx, next) =>
            {
                if (string.IsNullOrEmpty(expectedServiceKey))
                    return Results.Problem(
                        title: "Service key not configured",
                        detail: "Services:ServiceKey is required to enable the /api/v1/internal surface.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);

                var provided = ctx.HttpContext.Request.Headers[ServiceKeyHeader].FirstOrDefault();
                if (!string.Equals(provided, expectedServiceKey, StringComparison.Ordinal))
                    return Results.Unauthorized();

                return await next(ctx).ConfigureAwait(false);
            });

        group.MapGet("/agent-tenant/{agentId}", async (
            string agentId,
            CachedAgentTenantResolver resolver,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(agentId))
                return Results.BadRequest();

            var tenantId = await resolver.GetTenantIdAsync(agentId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(tenantId))
                return Results.NotFound();

            return Results.Json(
                new AgentTenantResponse(agentId, tenantId, DateTimeOffset.UtcNow),
                Verbara.Platform.Realtime.Contracts.RealtimeContractsJsonContext.Default.AgentTenantResponse);
        });

        group.MapPost("/hub-audit", async (
            HubAuditEntry entry,
            IAuditService auditService,
            CancellationToken ct) =>
        {
            if (entry is null || string.IsNullOrEmpty(entry.Action) || string.IsNullOrEmpty(entry.ActorTenantId))
                return Results.BadRequest();

            Dictionary<string, string>? metadata = null;
            if (!string.IsNullOrEmpty(entry.Metadata))
            {
                metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["raw"] = entry.Metadata,
                };
            }

            await auditService.RecordAsync(
                tenantId: new TenantId(entry.ActorTenantId),
                category: "auth",
                action: entry.Action,
                severity: "warning",
                actorId: entry.ActorId,
                actorType: "user",
                targetId: entry.TargetId,
                targetType: "Agent",
                correlationId: null,
                changes: null,
                metadata: metadata,
                ct: ct).ConfigureAwait(false);

            return Results.Accepted();
        });
    }
}
