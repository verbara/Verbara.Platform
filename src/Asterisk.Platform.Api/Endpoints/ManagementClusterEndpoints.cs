using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Middleware;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Pro.Cluster;
using Asterisk.Sdk.Pro.Cluster.Drain;
using Asterisk.Sdk.Pro.Cluster.Registry;
using Asterisk.Sdk.Pro.Cluster.Transport;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementClusterEndpoints
{
    private const string ClusterNotRegistered = "Cluster not registered";

    public static void MapManagementClusterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/cluster")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireLicenseFeature(LicenseFeature.Cluster);

        group.MapGet("/status", GetStatus);
        group.MapGet("/nodes", ListNodes);
        group.MapGet("/nodes/{nodeId}", GetNode);
        group.MapPost("/nodes/{nodeId}/drain", DrainNode);
        group.MapPost("/nodes", CreateNode);
        group.MapPut("/nodes/{nodeId}", UpdateNode);
        group.MapDelete("/nodes/{nodeId}", DeleteNode);
        group.MapDelete("/nodes/{nodeId}/drain", CancelDrain);
        group.MapPost("/nodes/{nodeId}/force-drain", ForceDrain);
        group.MapGet("/instances", ListInstances);
    }

    private static IResult GetStatus(IServiceProvider services)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Ok(new MgmtClusterStatusDto("local", [], 0, 0, [], []));

        var status = manager.GetStatus();
        return Results.Ok(new MgmtClusterStatusDto(
            status.InstanceId,
            status.Nodes.Select(MapNodeToDto).ToList(),
            status.TotalChannels,
            status.TotalAgents,
            status.ActiveDrains.Select(MapDrainToDto).ToList(),
            status.LiveInstances.Select(MapInstanceToDto).ToList()));
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
            return Results.Problem(ClusterNotRegistered, statusCode: 503);

        var options = new DrainOptions
        {
            Timeout = body.GracePeriodSeconds.HasValue
                ? TimeSpan.FromSeconds(body.GracePeriodSeconds.Value)
                : TimeSpan.FromMinutes(10),
        };

        var status = await manager.Drain.StartDrainAsync(nodeId, options, ct);
        return Results.Accepted($"/management/cluster/nodes/{nodeId}", MapDrainToDto(status));
    }

    private static async Task<IResult> CreateNode(
        [FromBody] CreateNodeRequest body,
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Problem(ClusterNotRegistered, statusCode: 503);

        var existing = manager.GetStatus().Nodes.FirstOrDefault(n => n.NodeId == body.NodeId);
        if (existing is not null)
            return Results.Conflict(new ErrorResponse($"Node '{body.NodeId}' already exists"));

        var nodeOptions = new ClusterNodeOptions
        {
            Ami = new AmiConnectionOptions
            {
                Hostname = body.AmiHostname,
                Port = body.AmiPort,
                Username = body.AmiUsername,
                Password = body.AmiPassword,
            },
            Weight = body.Weight,
            PriorityTier = body.PriorityTier,
            MaxCapacity = body.MaxCapacity,
            Tags = body.Tags,
        };

        await manager.AddNodeAsync(body.NodeId, nodeOptions, ct);

        var status = manager.GetStatus();
        var node = status.Nodes.FirstOrDefault(n => n.NodeId == body.NodeId);
        return node is not null
            ? Results.Created($"/management/cluster/nodes/{body.NodeId}", MapNodeToDto(node))
            : Results.Accepted($"/management/cluster/nodes/{body.NodeId}", (object?)null);
    }

    private static async Task<IResult> UpdateNode(
        string nodeId,
        [FromBody] UpdateNodeRequest body,
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Problem(ClusterNotRegistered, statusCode: 503);

        var existing = manager.GetStatus().Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (existing is null)
            return Results.NotFound(new ErrorResponse($"Node '{nodeId}' not found"));

        var tags = body.Tags is not null
            ? (IReadOnlyDictionary<string, string>)body.Tags
            : null;

        await manager.UpdateNodeAsync(nodeId, body.Weight, body.PriorityTier, body.MaxCapacity, tags, ct);

        var updated = manager.GetStatus().Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        return updated is not null
            ? Results.Ok(MapNodeToDto(updated))
            : Results.Ok(new ErrorResponse("Node updated but not found in status"));
    }

    private static async Task<IResult> DeleteNode(
        string nodeId,
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Problem(ClusterNotRegistered, statusCode: 503);

        var existing = manager.GetStatus().Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (existing is null)
            return Results.NotFound(new ErrorResponse($"Node '{nodeId}' not found"));

        if (existing.State is NodeState.Healthy or NodeState.Draining)
            return Results.BadRequest(new ErrorResponse($"Cannot delete node in '{existing.State}' state. Drain and wait for completion first."));

        await manager.RemoveNodeAsync(nodeId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CancelDrain(
        string nodeId,
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Problem(ClusterNotRegistered, statusCode: 503);

        var drainStatus = manager.Drain.GetDrainStatus(nodeId);
        if (drainStatus is null)
            return Results.NotFound(new ErrorResponse($"No active drain for node '{nodeId}'"));

        await manager.Drain.CancelDrainAsync(nodeId, ct);
        return Results.Ok(new StatusUpdateResponse(nodeId, "drain_cancelled"));
    }

    private static async Task<IResult> ForceDrain(
        string nodeId,
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Problem(ClusterNotRegistered, statusCode: 503);

        var drainStatus = manager.Drain.GetDrainStatus(nodeId);
        if (drainStatus is null)
            return Results.NotFound(new ErrorResponse($"No active drain for node '{nodeId}'"));

        await manager.Drain.ForceDrainAsync(nodeId, ct);
        return Results.Ok(new StatusUpdateResponse(nodeId, "force_drained"));
    }

    private static IResult ListInstances([FromServices] IServiceProvider services)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Ok(Array.Empty<MgmtInstanceDto>());

        var status = manager.GetStatus();
        return Results.Ok(status.LiveInstances.Select(MapInstanceToDto).ToList());
    }

    private static MgmtClusterNodeDto MapNodeToDto(ClusterNode n) =>
        new(n.NodeId, n.State.ToString().ToLowerInvariant(), n.Weight,
            n.PriorityTier, n.MaxCapacity, n.AsteriskVersion,
            n.StartupTime?.ToString("O"));

    private static MgmtDrainStatusDto MapDrainToDto(DrainStatus d) =>
        new(d.NodeId, d.State.ToString().ToLowerInvariant(),
            d.StartedAt, d.Deadline, d.InitialCallCount,
            d.RemainingCallCount, d.NaturallyCompleted, d.ForceDisconnected,
            d.EstimatedTimeToZero);

    private static MgmtInstanceDto MapInstanceToDto(InstanceInfo i) =>
        new(i.InstanceId, i.LastSeen, i.OwnedNodeIds, i.TotalChannels, i.TotalAgents);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record MgmtClusterStatusDto(
    string InstanceId,
    IReadOnlyList<MgmtClusterNodeDto> Nodes,
    int TotalChannels,
    int TotalAgents,
    IReadOnlyList<MgmtDrainStatusDto> ActiveDrains,
    IReadOnlyList<MgmtInstanceDto> Instances);

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
    int ForceDisconnected,
    TimeSpan? EstimatedTimeToZero);

internal sealed record CreateNodeRequest(
    string NodeId, string AmiHostname, int AmiPort,
    string AmiUsername, string AmiPassword,
    double Weight = 1.0, int PriorityTier = 0,
    int MaxCapacity = 500,
    Dictionary<string, string>? Tags = null);

internal sealed record UpdateNodeRequest(
    double? Weight, int? PriorityTier,
    int? MaxCapacity, Dictionary<string, string>? Tags);

internal sealed record MgmtInstanceDto(
    string InstanceId, DateTimeOffset LastSeen,
    IReadOnlyList<string> OwnedNodeIds,
    int TotalChannels, int TotalAgents);
