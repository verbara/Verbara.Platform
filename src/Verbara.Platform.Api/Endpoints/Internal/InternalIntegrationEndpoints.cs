using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Authz;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Realtime.Contracts.Dtos;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Http;

namespace Verbara.Platform.Api.Endpoints.Internal;

/// <summary>
/// Service-to-service endpoints consumed by Verbara.Platform.Realtime over
/// HTTP+JSON (X-Service-Key gated). Surfaces the two facades the Platform
/// Hub used to consume in-process (pre-ADR-0022) — cached agent→tenant
/// resolution and the hub audit sink — plus the account- and tenant-status lookup the
/// hub's connect filter makes for every connection.
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

        // Access lookup Realtime makes once per hub connection: the JWT it validates can outlive a
        // suspension (of the account or of its tenant) by up to its lifetime, so only the Api can say
        // whether the user may still connect. The decision covers account and tenant status:
        // User.CanAuthenticate, and a tenant that is serving (the sign-in rule of TenantServingGate:
        // Suspended, PendingDeletion and Deleted are refused). It is evaluated here, never re-derived by
        // the caller. A well-formed request always gets 200 with a decision — an unknown user is "not
        // allowed" — so Realtime can tell a definite refusal from an unreachable or older Api.
        group.MapGet("/user-access/{tenantId}/{userId}", async (
            string tenantId,
            string userId,
            IUserStore userStore,
            ITenantStore tenantStore,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(userId))
                return Results.BadRequest();

            var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct)
                .ConfigureAwait(false);
            var allowed = user is { CanAuthenticate: true }
                && await TenantServingGate.RefusedStatusAsync(tenantStore, tenantId, ct).ConfigureAwait(false) is null;

            return Results.Json(
                new UserAccessResponse(tenantId, userId, Allowed: allowed),
                Verbara.Platform.Realtime.Contracts.RealtimeContractsJsonContext.Default.UserAccessResponse);
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
