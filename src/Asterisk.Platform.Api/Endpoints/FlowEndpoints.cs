using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class FlowEndpoints
{
    public static void MapFlowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/flows")
            .RequireAuthorization("AdminOnly")
            .RequirePlanFeature(PlanFeature.Flows);

        group.MapGet("/", ListFlows);
        group.MapGet("/{id}", GetFlow);
        group.MapPost("/", CreateFlow);
        group.MapPut("/{id}", UpdateFlow);
        group.MapPost("/{id}/publish", PublishFlow);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListFlows(
        HttpContext context,
        [FromServices] IFlowStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var flows = await store.ListAsync(tenantId, ct);
        return Results.Ok(flows);
    }

    private static async Task<IResult> GetFlow(
        string id,
        HttpContext context,
        [FromServices] IFlowStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var flow = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return flow is null ? Results.NotFound() : Results.Ok(flow);
    }

    private static async Task<IResult> CreateFlow(
        HttpContext context,
        [FromBody] CreateFlowRequest body,
        [FromServices] IFlowStore store,
        IClock clock,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var nodes = MapNodes(body.Nodes ?? []);

        // Allow creating an empty "scaffold" flow (e.g. from the UI's
        // "New Flow" button) — auto-generate an entry node id when the
        // caller hasn't provided one or any nodes yet.
        var entryNodeId = string.IsNullOrWhiteSpace(body.EntryNodeId)
            ? EntityId.New()
            : EntityId.From(body.EntryNodeId);

        var flow = new FlowDefinition
        {
            FlowId = EntityId.New(),
            TenantId = tenantId,
            Name = body.Name,
            Version = 1,
            IsPublished = false,
            EntryNodeId = entryNodeId,
            Nodes = nodes,
            CreatedAt = clock.UtcNow,
        };
        await store.SaveAsync(flow, ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "flow.created", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: flow.FlowId.Value, targetType: "flow",
            changes: new AuditChanges(Before: null, After: new { flow.FlowId, flow.Name }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/admin/flows/{flow.FlowId}", flow);
    }

    private static async Task<IResult> UpdateFlow(
        string id,
        HttpContext context,
        [FromBody] UpdateFlowRequest body,
        [FromServices] IFlowStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

        var before = new { existing.FlowId, existing.Name, existing.Version };

        if (body.Name is not null) existing.Name = body.Name;

        var updated = new FlowDefinition
        {
            FlowId = existing.FlowId,
            TenantId = existing.TenantId,
            Name = existing.Name,
            Version = existing.Version,
            IsPublished = existing.IsPublished,
            EntryNodeId = body.EntryNodeId is not null ? EntityId.From(body.EntryNodeId) : existing.EntryNodeId,
            Nodes = body.Nodes is not null ? MapNodes(body.Nodes) : existing.Nodes,
            CreatedAt = existing.CreatedAt,
        };

        await store.SaveAsync(updated, ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "flow.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "flow",
            changes: new AuditChanges(Before: before, After: new { updated.FlowId, updated.Name, updated.Version }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(updated);
    }

    private static async Task<IResult> PublishFlow(
        string id,
        HttpContext context,
        [FromServices] IFlowStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

        var published = new FlowDefinition
        {
            FlowId = existing.FlowId,
            TenantId = existing.TenantId,
            Name = existing.Name,
            Version = existing.Version + 1,
            IsPublished = true,
            EntryNodeId = existing.EntryNodeId,
            Nodes = existing.Nodes,
            CreatedAt = existing.CreatedAt,
        };

        await store.SaveAsync(published, ct);
        return Results.Ok(published);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private static List<FlowNode> MapNodes(IReadOnlyList<FlowNodeDto> dtos) =>
        dtos.Select(n => new FlowNode
        {
            NodeId = EntityId.From(n.NodeId),
            Type = n.Type,
            Config = n.Config ?? new Dictionary<string, string>(),
            Edges = n.Edges?.Select(e => new FlowEdge(e.Condition, EntityId.From(e.TargetNodeId))).ToList()
                    ?? (IReadOnlyList<FlowEdge>)[],
        }).ToList();

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record CreateFlowRequest(
    string Name,
    string? EntryNodeId = null,
    IReadOnlyList<FlowNodeDto>? Nodes = null);

internal sealed record UpdateFlowRequest(
    string? Name = null,
    string? EntryNodeId = null,
    IReadOnlyList<FlowNodeDto>? Nodes = null);

internal sealed record FlowNodeDto(
    string NodeId,
    string Type,
    Dictionary<string, string>? Config = null,
    IReadOnlyList<FlowEdgeDto>? Edges = null);

internal sealed record FlowEdgeDto(
    string Condition,
    string TargetNodeId);
