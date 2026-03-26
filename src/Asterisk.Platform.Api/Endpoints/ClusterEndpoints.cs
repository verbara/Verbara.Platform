using Asterisk.Sdk.Pro.Cluster;
using Asterisk.Sdk.Pro.Cluster.Drain;
using Asterisk.Sdk.Pro.Cluster.Registry;
using Asterisk.Sdk.Pro.Cluster.Transport;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ClusterEndpoints
{
    public static void MapClusterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/cluster").RequireAuthorization("AdminOnly");

        group.MapGet("/status", GetStatus);
        group.MapGet("/nodes", ListNodes);
        group.MapGet("/nodes/{nodeId}", GetNode);
        group.MapPost("/nodes/{nodeId}/drain", DrainNode);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────

    private static IResult GetStatus(IServiceProvider services)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Ok(new ClusterStatusDto("local", [], 0, 0, []));

        var status = manager.GetStatus();
        return Results.Ok(new ClusterStatusDto(
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
            return Results.Ok(Array.Empty<ClusterNodeDto>());

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
        DrainNodeRequest body,
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
        return Results.Accepted($"/api/admin/cluster/nodes/{nodeId}", MapDrainToDto(status));
    }

    // ─── Mapping ─────────────────────────────────────────────────────────────

    private static ClusterNodeDto MapNodeToDto(ClusterNode n) =>
        new(n.NodeId, n.State.ToString().ToLowerInvariant(), n.Weight,
            n.PriorityTier, n.MaxCapacity, n.AsteriskVersion,
            n.StartupTime?.ToString("O"));

    private static DrainStatusDto MapDrainToDto(DrainStatus d) =>
        new(d.NodeId, d.State.ToString().ToLowerInvariant(),
            d.StartedAt, d.Deadline, d.InitialCallCount,
            d.RemainingCallCount, d.NaturallyCompleted, d.ForceDisconnected);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record ClusterStatusDto(
    string InstanceId,
    IReadOnlyList<ClusterNodeDto> Nodes,
    int TotalChannels,
    int TotalAgents,
    IReadOnlyList<DrainStatusDto> ActiveDrains);

internal sealed record ClusterNodeDto(
    string NodeId,
    string State,
    double Weight,
    int PriorityTier,
    int MaxCapacity,
    string? AsteriskVersion,
    string? StartupTime);

internal sealed record DrainNodeRequest(int? GracePeriodSeconds);

internal sealed record DrainStatusDto(
    string NodeId,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    int InitialCallCount,
    int RemainingCallCount,
    int NaturallyCompleted,
    int ForceDisconnected);
