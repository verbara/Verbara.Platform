using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class FlowEndpoints
{
    public static void MapFlowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/flows").RequireAuthorization("AdminOnly");

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
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var nodes = MapNodes(body.Nodes);
        var flow = new FlowDefinition
        {
            FlowId = EntityId.New(),
            TenantId = tenantId,
            Name = body.Name,
            Version = 1,
            IsPublished = false,
            EntryNodeId = EntityId.From(body.EntryNodeId),
            Nodes = nodes,
            CreatedAt = clock.UtcNow,
        };
        await store.SaveAsync(flow, ct);
        return Results.Created($"/admin/flows/{flow.FlowId}", flow);
    }

    private static async Task<IResult> UpdateFlow(
        string id,
        HttpContext context,
        [FromBody] UpdateFlowRequest body,
        [FromServices] IFlowStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

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
    string EntryNodeId,
    IReadOnlyList<FlowNodeDto> Nodes);

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
