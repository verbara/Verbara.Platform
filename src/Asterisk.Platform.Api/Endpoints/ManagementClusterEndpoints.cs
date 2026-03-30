using Asterisk.Sdk.Pro.Cluster;
using Asterisk.Sdk.Pro.Cluster.Drain;
using Asterisk.Sdk.Pro.Cluster.Registry;
using Asterisk.Sdk.Pro.Cluster.Transport;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementClusterEndpoints
{
    public static void MapManagementClusterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/cluster").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/status", GetStatus);
        group.MapGet("/nodes", ListNodes);
        group.MapGet("/nodes/{nodeId}", GetNode);
        group.MapPost("/nodes/{nodeId}/drain", DrainNode);
    }

    private static IResult GetStatus(IServiceProvider services)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Ok(new MgmtClusterStatusDto("local", [], 0, 0, []));

        var status = manager.GetStatus();
        return Results.Ok(new MgmtClusterStatusDto(
            status.InstanceId,
            status.Nodes.Select(MapNodeToDto).ToList(),
            status.TotalChannels,
            status.TotalAgents,
            status.ActiveDrains.Select(MapDrainToDto).ToList()));
    }

    private static async Task<IResult> ListNodes(IServiceProvider services, CancellationToken ct)
    {
        var transport = services.GetService<ClusterTransportBase>();
        if (transport is null)
            return Results.Ok(Array.Empty<MgmtClusterNodeDto>());

        var nodes = await transport.GetNodesAsync(ct);
        return Results.Ok(nodes.Select(MapNodeToDto).ToList());
    }

    private static async Task<IResult> GetNode(string nodeId, IServiceProvider services, CancellationToken ct)
    {
        var transport = services.GetService<ClusterTransportBase>();
        if (transport is null)
            return Results.NotFound();

        var nodes = await transport.GetNodesAsync(ct);
        var node = nodes.FirstOrDefault(n => n.NodeId == nodeId);
        return node is null ? Results.NotFound() : Results.Ok(MapNodeToDto(node));
    }

    private static async Task<IResult> DrainNode(
        string nodeId,
        [FromBody] MgmtDrainNodeRequest body,
        IServiceProvider services,
        CancellationToken ct)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Problem("Cluster not registered", statusCode: 503);

        var options = new DrainOptions
        {
            Timeout = body.GracePeriodSeconds.HasValue
                ? TimeSpan.FromSeconds(body.GracePeriodSeconds.Value)
                : TimeSpan.FromMinutes(10),
        };

        var status = await manager.Drain.StartDrainAsync(nodeId, options, ct);
        return Results.Accepted($"/api/management/cluster/nodes/{nodeId}", MapDrainToDto(status));
    }

    private static MgmtClusterNodeDto MapNodeToDto(ClusterNode n) =>
        new(n.NodeId, n.State.ToString().ToLowerInvariant(), n.Weight,
            n.PriorityTier, n.MaxCapacity, n.AsteriskVersion,
            n.StartupTime?.ToString("O"));

    private static MgmtDrainStatusDto MapDrainToDto(DrainStatus d) =>
        new(d.NodeId, d.State.ToString().ToLowerInvariant(),
            d.StartedAt, d.Deadline, d.InitialCallCount,
            d.RemainingCallCount, d.NaturallyCompleted, d.ForceDisconnected);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record MgmtClusterStatusDto(
    string InstanceId,
    IReadOnlyList<MgmtClusterNodeDto> Nodes,
    int TotalChannels,
    int TotalAgents,
    IReadOnlyList<MgmtDrainStatusDto> ActiveDrains);

internal sealed record MgmtClusterNodeDto(
    string NodeId,
    string State,
    double Weight,
    int PriorityTier,
    int MaxCapacity,
    string? AsteriskVersion,
    string? StartupTime);

internal sealed record MgmtDrainNodeRequest(int? GracePeriodSeconds);

internal sealed record MgmtDrainStatusDto(
    string NodeId,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    int InitialCallCount,
    int RemainingCallCount,
    int NaturallyCompleted,
    int ForceDisconnected);
