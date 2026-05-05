# Plan 29C: Server Management API

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose 6 new REST endpoints for runtime cluster node CRUD and drain lifecycle, consuming `ClusterManager` SDK methods. Update existing DTOs to include `LiveInstances` and `EstimatedTimeToZero`.

**Architecture:** Extend `ManagementClusterEndpoints.cs` with 6 new routes. All require `PlatformAdminOnly` authorization. New DTOs registered in `ApiJsonContext`. Thin layer — all logic delegated to SDK's `ClusterManager` and `DrainManager`.

**Tech Stack:** .NET 10 Native AOT, Minimal API, Dapper.

**Spec:** `docs/superpowers/specs/2026-03-31-v121-operations-design.md` — Sub-project B.

**Prerequisite:** Plan 29B complete (PostgresClusterTransport + UpdateNodeAsync in SDK Pro). Must `dotnet restore` in Platform after SDK pack.

---

### Task 1: Restore SDK Pro package + add new DTOs

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementClusterEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

- [ ] **Step 1: Restore updated SDK Pro package**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform
dotnet restore
dotnet build src/Asterisk.Platform.Api/
```

Expected: Build succeeds with new `ClusterManager.UpdateNodeAsync()` and `NodeUpdate` available.

- [ ] **Step 2: Add new DTOs to ManagementClusterEndpoints.cs**

Add at the bottom of the file, alongside existing DTOs:

```csharp
internal sealed record CreateNodeRequest(
    string NodeId,
    string AmiHostname,
    int AmiPort,
    string AmiUsername,
    string AmiPassword,
    double Weight = 1.0,
    int PriorityTier = 0,
    int MaxCapacity = 500,
    Dictionary<string, string>? Tags = null);

internal sealed record UpdateNodeRequest(
    double? Weight,
    int? PriorityTier,
    int? MaxCapacity,
    Dictionary<string, string>? Tags);

internal sealed record MgmtInstanceDto(
    string InstanceId,
    DateTimeOffset LastSeen,
    IReadOnlyList<string> OwnedNodeIds,
    int TotalChannels,
    int TotalAgents);
```

- [ ] **Step 3: Update existing DTOs**

Update `MgmtClusterStatusDto` to add Instances:

```csharp
// Replace existing MgmtClusterStatusDto with:
internal sealed record MgmtClusterStatusDto(
    string InstanceId,
    IReadOnlyList<MgmtClusterNodeDto> Nodes,
    int TotalChannels,
    int TotalAgents,
    IReadOnlyList<MgmtDrainStatusDto> ActiveDrains,
    IReadOnlyList<MgmtInstanceDto> Instances);
```

Update `MgmtDrainStatusDto` to add EstimatedTimeToZero:

```csharp
// Replace existing MgmtDrainStatusDto with:
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
```

- [ ] **Step 4: Register new DTOs in ApiJsonContext**

```csharp
[JsonSerializable(typeof(CreateNodeRequest))]
[JsonSerializable(typeof(UpdateNodeRequest))]
[JsonSerializable(typeof(MgmtInstanceDto))]
[JsonSerializable(typeof(List<MgmtInstanceDto>))]
```

- [ ] **Step 5: Update GetStatus mapping to include Instances and EstimatedTimeToZero**

Update the `GetStatus` method body to map `LiveInstances` and include `EstimatedTimeToZero` in drain DTOs. Also update the `DrainNode` response mapping.

- [ ] **Step 6: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementClusterEndpoints.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "feat: add cluster management DTOs with Instances and EstimatedTimeToZero"
```

---

### Task 2: Add 6 new endpoints

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementClusterEndpoints.cs`

- [ ] **Step 1: Add route mappings**

In the `MapManagementClusterEndpoints` method, after existing route mappings, add:

```csharp
group.MapPost("/nodes", CreateNode);
group.MapPut("/nodes/{nodeId}", UpdateNode);
group.MapDelete("/nodes/{nodeId}", DeleteNode);
group.MapDelete("/nodes/{nodeId}/drain", CancelDrain);
group.MapPost("/nodes/{nodeId}/force-drain", ForceDrain);
group.MapGet("/instances", ListInstances);
```

- [ ] **Step 2: Implement CreateNode**

```csharp
private static async Task<IResult> CreateNode(
    [FromBody] CreateNodeRequest body,
    [FromServices] ClusterManager cluster,
    CancellationToken ct)
{
    var existingNode = cluster.Registry.GetNode(body.NodeId);
    if (existingNode is not null)
        return Results.Conflict(new ErrorResponse($"Node '{body.NodeId}' already exists"));

    var amiOptions = new AmiConnectionOptions
    {
        Hostname = body.AmiHostname,
        Port = body.AmiPort,
        Username = body.AmiUsername,
        Password = body.AmiPassword
    };

    var node = await cluster.AddNodeAsync(
        body.NodeId, amiOptions, body.Weight, body.PriorityTier,
        body.MaxCapacity, body.Tags?.AsReadOnly(), ct);

    return Results.Created($"/api/management/cluster/nodes/{node.NodeId}",
        MapToDto(node));
}
```

- [ ] **Step 3: Implement UpdateNode**

```csharp
private static async Task<IResult> UpdateNode(
    string nodeId,
    [FromBody] UpdateNodeRequest body,
    [FromServices] ClusterManager cluster,
    CancellationToken ct)
{
    var node = cluster.Registry.GetNode(nodeId);
    if (node is null)
        return Results.NotFound(new ErrorResponse($"Node '{nodeId}' not found"));

    await cluster.UpdateNodeAsync(
        nodeId, body.Weight, body.PriorityTier, body.MaxCapacity,
        body.Tags?.AsReadOnly(), ct);

    var updated = cluster.Registry.GetNode(nodeId)!;
    return Results.Ok(MapToDto(updated));
}
```

- [ ] **Step 4: Implement DeleteNode**

```csharp
private static async Task<IResult> DeleteNode(
    string nodeId,
    [FromServices] ClusterManager cluster,
    CancellationToken ct)
{
    var node = cluster.Registry.GetNode(nodeId);
    if (node is null)
        return Results.NotFound(new ErrorResponse($"Node '{nodeId}' not found"));

    if (node.State is NodeState.Healthy or NodeState.Draining)
        return Results.BadRequest(new ErrorResponse(
            $"Node '{nodeId}' is {node.State}. Drain it first before removing."));

    await cluster.RemoveNodeAsync(nodeId, ct);
    return Results.NoContent();
}
```

- [ ] **Step 5: Implement CancelDrain**

```csharp
private static async Task<IResult> CancelDrain(
    string nodeId,
    [FromServices] ClusterManager cluster,
    CancellationToken ct)
{
    var status = cluster.Drain.GetDrainStatus(nodeId);
    if (status is null)
        return Results.NotFound(new ErrorResponse($"No active drain for node '{nodeId}'"));

    await cluster.Drain.CancelDrainAsync(nodeId, ct);
    return Results.NoContent();
}
```

- [ ] **Step 6: Implement ForceDrain**

```csharp
private static async Task<IResult> ForceDrain(
    string nodeId,
    [FromServices] ClusterManager cluster,
    CancellationToken ct)
{
    var status = cluster.Drain.GetDrainStatus(nodeId);
    if (status is null)
        return Results.NotFound(new ErrorResponse($"No active drain for node '{nodeId}'"));

    await cluster.Drain.ForceDrainAsync(nodeId, ct);
    return Results.NoContent();
}
```

- [ ] **Step 7: Implement ListInstances**

```csharp
private static async Task<IResult> ListInstances(
    [FromServices] ClusterManager cluster,
    CancellationToken ct)
{
    var status = cluster.GetStatus();
    var instances = status.LiveInstances.Select(i => new MgmtInstanceDto(
        i.InstanceId, i.LastSeen, i.OwnedNodeIds.ToList(),
        i.TotalChannels, i.TotalAgents)).ToList();
    return Results.Ok(instances);
}
```

- [ ] **Step 8: Add required usings**

```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Sdk.Pro.Cluster;
using Asterisk.Sdk.Pro.Cluster.Registry;
using Asterisk.Sdk.Pro.Cluster.Transport;
using Asterisk.Sdk.Ami;
```

- [ ] **Step 9: Extract MapToDto helper (if not already existing)**

```csharp
private static MgmtClusterNodeDto MapToDto(ClusterNode n) => new(
    n.NodeId, n.State.ToString(), n.Weight, n.PriorityTier,
    n.MaxCapacity, n.AsteriskVersion, n.StartupTime?.ToString("o"));
```

- [ ] **Step 10: Verify build and run tests**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: Build succeeds, all tests pass.

- [ ] **Step 11: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementClusterEndpoints.cs
git commit -m "feat: add 6 cluster management endpoints (CRUD nodes, cancel/force drain, instances)"
```

---

### Task 3: Wire PostgresClusterTransport in Program.cs

**Files:**
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Add conditional PostgresClusterTransport registration**

After the existing `AddAsteriskCluster` call, add:

```csharp
var clusterConn = builder.Configuration.GetConnectionString("Cluster")
    ?? builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrEmpty(clusterConn))
{
    builder.Services.UsePostgresClusterTransport(clusterConn);
}
```

Add using:
```csharp
using Asterisk.Sdk.Pro.Cluster.Transport;
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Program.cs
git commit -m "feat: wire PostgresClusterTransport when Cluster or Postgres connection string present"
```

---

### Task 4: Tests for new endpoints

**Files:**
- Create or modify: `tests/Asterisk.Platform.Api.Tests/ManagementClusterEndpointTests.cs`

- [ ] **Step 1: Write tests**

Add tests for the 6 new endpoints. Since ClusterManager is complex to mock (not interface-based), test via integration or via validation logic:

```csharp
[Fact]
public void CreateNodeRequest_ShouldHaveRequiredFields()
{
    var request = new CreateNodeRequest(
        "node-1", "10.0.0.1", 5038, "admin", "secret");
    request.NodeId.Should().Be("node-1");
    request.Weight.Should().Be(1.0);
    request.PriorityTier.Should().Be(0);
    request.MaxCapacity.Should().Be(500);
}

[Fact]
public void UpdateNodeRequest_ShouldAllowPartialUpdates()
{
    var request = new UpdateNodeRequest(Weight: 2.5, null, null, null);
    request.Weight.Should().Be(2.5);
    request.PriorityTier.Should().BeNull();
}

[Fact]
public void MgmtInstanceDto_ShouldMapCorrectly()
{
    var dto = new MgmtInstanceDto(
        "api-1", DateTimeOffset.UtcNow,
        new List<string> { "node-1", "node-2" }, 100, 10);
    dto.OwnedNodeIds.Should().HaveCount(2);
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ -v q`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Asterisk.Platform.Api.Tests/
git commit -m "test: add cluster management endpoint DTO tests"
```
