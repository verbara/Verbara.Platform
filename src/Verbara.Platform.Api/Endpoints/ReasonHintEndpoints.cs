using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// Admin CRUD for <see cref="ReasonHint"/> — the implicit-capture rule mapping a
/// routing/entry context (scope → scopeRef) to a captured reason taxonomy path so
/// the disposition form can be prefilled. License-gated behind
/// <see cref="LicenseFeature.AdvancedTypification"/>, mirroring the rest of the
/// typification admin surface. Uses TEXT EntityId ids (like queues/agents).
/// </summary>
internal static class ReasonHintEndpoints
{
    public static void MapReasonHintEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/reason-hints")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.AdvancedTypification);

        group.MapGet("/", List);
        group.MapGet("/{id}", Get);
        group.MapPost("/", Create);
        group.MapPut("/{id}", Update);
        group.MapDelete("/{id}", Delete);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────

    private static async Task<IResult> List(
        HttpContext context,
        [FromServices] IReasonHintStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListAsync(tenantId, ct);
        return Results.Ok(items.Select(MapToDto).ToArray());
    }

    private static async Task<IResult> Get(
        string id,
        HttpContext context,
        [FromServices] IReasonHintStore store,
        CancellationToken ct)
    {
        if (!EntityId.IsValid(id))
            return Results.NotFound();

        var tenantId = GetTenantId(context);
        var hint = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return hint is null ? Results.NotFound() : Results.Ok(MapToDto(hint));
    }

    private static async Task<IResult> Create(
        HttpContext context,
        [FromBody] CreateReasonHintRequest body,
        [FromServices] IReasonHintStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        if (!Enum.TryParse<ReasonHintScope>(body.Scope, ignoreCase: true, out var scope))
            return Results.BadRequest(new ErrorResponse($"Unknown reason-hint scope '{body.Scope}'."));

        if (string.IsNullOrWhiteSpace(body.ScopeRef))
            return Results.BadRequest(new ErrorResponse("ScopeRef is required."));

        // ReasonPath is the captured node-Codes JSON (e.g. ["CITAS","REPROG"]).
        // Validate shape (non-empty JSON array of strings) so a malformed value can't be
        // stored to silently degrade at the resolver. Deep schema validation (do these
        // codes exist in a published schema?) remains out of scope for this rule editor.
        if (string.IsNullOrWhiteSpace(body.ReasonPath))
            return Results.BadRequest(new ErrorResponse("ReasonPath is required."));

        if (!IsValidReasonPath(body.ReasonPath))
            return Results.BadRequest(new ErrorResponse(
                "ReasonPath must be a non-empty JSON array of node codes (e.g. [\"CITAS\",\"REPROG\"])."));

        var hint = new ReasonHint
        {
            HintId = EntityId.New(),
            TenantId = tenantId,
            Scope = scope,
            ScopeRef = body.ScopeRef,
            ReasonPath = body.ReasonPath,
            Priority = body.Priority,
            IsActive = body.IsActive,
        };

        await store.SaveAsync(hint, ct);

        var dto = MapToDto(hint);
        await RecordAudit(context, audit, tenantId, "reason_hint.created",
            targetId: hint.HintId.Value, before: null, after: dto, ct);

        return Results.Created($"/api/v1/admin/reason-hints/{hint.HintId.Value}", dto);
    }

    private static async Task<IResult> Update(
        string id,
        HttpContext context,
        [FromBody] UpdateReasonHintRequest body,
        [FromServices] IReasonHintStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        if (!EntityId.IsValid(id))
            return Results.NotFound();

        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

        var before = MapToDto(existing);

        var scope = existing.Scope;
        if (body.Scope is not null)
        {
            if (!Enum.TryParse<ReasonHintScope>(body.Scope, ignoreCase: true, out scope))
                return Results.BadRequest(new ErrorResponse($"Unknown reason-hint scope '{body.Scope}'."));
        }

        if (body.ScopeRef is not null && string.IsNullOrWhiteSpace(body.ScopeRef))
            return Results.BadRequest(new ErrorResponse("ScopeRef is required."));

        if (body.ReasonPath is not null)
        {
            if (string.IsNullOrWhiteSpace(body.ReasonPath))
                return Results.BadRequest(new ErrorResponse("ReasonPath is required."));

            if (!IsValidReasonPath(body.ReasonPath))
                return Results.BadRequest(new ErrorResponse(
                    "ReasonPath must be a non-empty JSON array of node codes (e.g. [\"CITAS\",\"REPROG\"])."));
        }

        var updated = existing with
        {
            Scope = scope,
            ScopeRef = body.ScopeRef ?? existing.ScopeRef,
            ReasonPath = body.ReasonPath ?? existing.ReasonPath,
            Priority = body.Priority ?? existing.Priority,
            IsActive = body.IsActive ?? existing.IsActive,
        };

        await store.SaveAsync(updated, ct);

        var after = MapToDto(updated);
        await RecordAudit(context, audit, tenantId, "reason_hint.updated",
            targetId: updated.HintId.Value, before: before, after: after, ct);

        return Results.Ok(after);
    }

    private static async Task<IResult> Delete(
        string id,
        HttpContext context,
        [FromServices] IReasonHintStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        if (!EntityId.IsValid(id))
            return Results.NotFound();

        var tenantId = GetTenantId(context);
        var hintId = EntityId.From(id);
        var before = await store.GetByIdAsync(tenantId, hintId, ct);
        if (before is null)
            return Results.NotFound();

        await store.DeleteAsync(tenantId, hintId, ct);
        await RecordAudit(context, audit, tenantId, "reason_hint.deleted",
            targetId: hintId.Value, before: MapToDto(before), after: null, ct);

        return Results.NoContent();
    }

    // ─── Mapping helpers ──────────────────────────────────────────────────────

    private static ReasonHintDto MapToDto(ReasonHint h) =>
        new(
            Id: h.HintId.Value,
            Scope: h.Scope.ToString(),
            ScopeRef: h.ScopeRef,
            ReasonPath: h.ReasonPath,
            Priority: h.Priority,
            IsActive: h.IsActive);

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="reasonPath"/> parses to a non-empty JSON array of strings,
    /// each non-empty — the shape the prefill resolver consumes. Uses the AOT-safe source-gen
    /// <see cref="TypificationJsonContext"/> (no reflection). A <see cref="JsonException"/> on a
    /// malformed value (e.g. plain <c>"CITAS"</c>) is treated as invalid.
    /// </summary>
    private static bool IsValidReasonPath(string reasonPath)
    {
        string[]? codes;
        try
        {
            codes = JsonSerializer.Deserialize(reasonPath, TypificationJsonContext.Default.StringArray);
        }
        catch (JsonException)
        {
            return false;
        }

        if (codes is null || codes.Length == 0)
            return false;

        foreach (var code in codes)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;
        }

        return true;
    }

    private static Task RecordAudit(
        HttpContext context,
        IAuditService audit,
        TenantId tenantId,
        string action,
        string targetId,
        object? before,
        object? after,
        CancellationToken ct) =>
        audit.RecordAsync(
            tenantId, category: "config", action: action, severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: targetId, targetType: "reason_hint",
            changes: new AuditChanges(Before: before, After: after),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── DTOs ───────────────────────────────────────────────────────────────────────

internal sealed record ReasonHintDto(
    string Id, string Scope, string ScopeRef, string ReasonPath, int Priority, bool IsActive);

internal sealed record CreateReasonHintRequest(
    string Scope, string ScopeRef, string ReasonPath, int Priority, bool IsActive);

internal sealed record UpdateReasonHintRequest(
    string? Scope, string? ScopeRef, string? ReasonPath, int? Priority, bool? IsActive);
