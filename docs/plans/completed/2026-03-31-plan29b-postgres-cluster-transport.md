# Plan 29B: PostgresClusterTransport

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a PostgreSQL-backed `ClusterTransportBase` enabling multiple Platform API instances to share cluster state (nodes, drains, sessions, locks, heartbeats) via persistent storage and LISTEN/NOTIFY pub/sub.

**Architecture:** Single new file `PostgresClusterTransport.cs` implementing all 18 abstract methods from `ClusterTransportBase`. Uses Dapper for queries, Npgsql LISTEN/NOTIFY for pub/sub, `EnsureSchemaAsync()` for auto-migration. DI extension `UsePostgresClusterTransport(connectionString)`.

**Tech Stack:** .NET 10 Native AOT, Npgsql 9.0.3, Dapper 2.1.66, PostgreSQL 16+.

**Spec:** `docs/superpowers/specs/2026-03-31-v121-operations-design.md` — Sub-project A.

**Repo:** `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/`

**Reference implementation:** `Asterisk.Sdk.Pro.Cluster/Transport/InMemoryClusterTransport.cs` — follow the same method signatures and behavior, but persist to PostgreSQL.

---

### Task 1: Schema SQL embedded resource

**Files:**
- Create: `src/Asterisk.Sdk.Pro.Cluster/Transport/001_ClusterSchema.sql`

- [ ] **Step 1: Create the migration SQL**

```sql
-- 001_ClusterSchema.sql
-- Cluster transport persistence for multi-instance deployments

CREATE TABLE IF NOT EXISTS cluster_nodes (
    node_id          TEXT PRIMARY KEY,
    ami_hostname     TEXT NOT NULL,
    ami_port         INT NOT NULL DEFAULT 5038,
    ami_username     TEXT NOT NULL,
    ami_password     TEXT NOT NULL,
    state            TEXT NOT NULL DEFAULT 'Unknown',
    owner_instance   TEXT,
    generation       BIGINT NOT NULL DEFAULT 0,
    weight           DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    priority_tier    INT NOT NULL DEFAULT 0,
    max_capacity     INT NOT NULL DEFAULT 500,
    tags             JSONB,
    asterisk_version TEXT,
    startup_time     TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS cluster_instances (
    instance_id      TEXT PRIMARY KEY,
    owned_node_ids   JSONB NOT NULL DEFAULT '[]',
    total_channels   INT NOT NULL DEFAULT 0,
    total_agents     INT NOT NULL DEFAULT 0,
    last_seen        TIMESTAMPTZ NOT NULL,
    expires_at       TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS cluster_session_snapshots (
    server_id        TEXT NOT NULL,
    linked_id        TEXT NOT NULL,
    session_id       TEXT NOT NULL,
    state            TEXT NOT NULL,
    direction        TEXT NOT NULL,
    created_at       TIMESTAMPTZ NOT NULL,
    queue_name       TEXT,
    agent_id         TEXT,
    bridge_id        TEXT,
    hold_time        INTERVAL,
    metadata         JSONB,
    PRIMARY KEY (server_id, linked_id)
);

CREATE TABLE IF NOT EXISTS cluster_drain_states (
    node_id              TEXT PRIMARY KEY,
    state                TEXT NOT NULL,
    started_at           TIMESTAMPTZ NOT NULL,
    deadline             TIMESTAMPTZ NOT NULL,
    initial_call_count   INT NOT NULL DEFAULT 0,
    remaining_call_count INT NOT NULL DEFAULT 0,
    naturally_completed  INT NOT NULL DEFAULT 0,
    force_disconnected   INT NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS cluster_locks (
    resource         TEXT PRIMARY KEY,
    owner            TEXT NOT NULL,
    expires_at       TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS cluster_generations (
    node_id          TEXT PRIMARY KEY,
    generation       BIGINT NOT NULL DEFAULT 0
);
```

- [ ] **Step 2: Mark as embedded resource in .csproj**

Add to `Asterisk.Sdk.Pro.Cluster.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Transport\001_ClusterSchema.sql" />
</ItemGroup>
```

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Sdk.Pro.Cluster/Transport/001_ClusterSchema.sql src/Asterisk.Sdk.Pro.Cluster/Asterisk.Sdk.Pro.Cluster.csproj
git commit -m "feat: add cluster transport PostgreSQL schema"
```

---

### Task 2: PostgresClusterTransport — Node Registry + Locking

**Files:**
- Create: `src/Asterisk.Sdk.Pro.Cluster/Transport/PostgresClusterTransport.cs`

- [ ] **Step 1: Create the file with constructor, EnsureSchema, and node registry methods**

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Sdk.Pro.Cluster.Drain;
using Asterisk.Sdk.Pro.Cluster.Registry;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Asterisk.Sdk.Pro.Cluster.Transport;

public sealed partial class PostgresClusterTransport : ClusterTransportBase
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresClusterTransport> _logger;
    private readonly List<Channel<ClusterEvent>> _subscribers = [];
    private readonly Lock _subscriberLock = new();
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    public PostgresClusterTransport(
        string connectionString,
        ILogger<PostgresClusterTransport> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string checkTable = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '_schema_cluster')";
        var exists = await conn.ExecuteScalarAsync<bool>(checkTable);

        if (exists) return;

        var assembly = typeof(PostgresClusterTransport).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("001_ClusterSchema.sql", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct);

        await conn.ExecuteAsync(sql);
        await conn.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS _schema_cluster (version INT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())");
        await conn.ExecuteAsync(
            "INSERT INTO _schema_cluster (version) VALUES (1) ON CONFLICT DO NOTHING");
    }

    // ── Node Registry ────────────────────────────────────────────

    public override async ValueTask RegisterNodeAsync(
        ClusterNode node, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            INSERT INTO cluster_nodes (node_id, ami_hostname, ami_port, ami_username, ami_password,
                                       state, owner_instance, generation, weight, priority_tier,
                                       max_capacity, tags, asterisk_version, startup_time)
            VALUES (@NodeId, @AmiHostname, @AmiPort, @AmiUsername, @AmiPassword,
                    @State, @OwnerInstance, @Generation, @Weight, @PriorityTier,
                    @MaxCapacity, @Tags::jsonb, @AsteriskVersion, @StartupTime)
            ON CONFLICT (node_id) DO UPDATE SET
                ami_hostname = EXCLUDED.ami_hostname, ami_port = EXCLUDED.ami_port,
                ami_username = EXCLUDED.ami_username, ami_password = EXCLUDED.ami_password,
                state = EXCLUDED.state, owner_instance = EXCLUDED.owner_instance,
                generation = EXCLUDED.generation, weight = EXCLUDED.weight,
                priority_tier = EXCLUDED.priority_tier, max_capacity = EXCLUDED.max_capacity,
                tags = EXCLUDED.tags, asterisk_version = EXCLUDED.asterisk_version,
                startup_time = EXCLUDED.startup_time
            """, new
        {
            node.NodeId,
            AmiHostname = node.AmiOptions.Hostname,
            AmiPort = node.AmiOptions.Port,
            AmiUsername = node.AmiOptions.Username,
            AmiPassword = node.AmiOptions.Password,
            State = node.State.ToString(),
            OwnerInstance = node.OwnerInstanceId,
            node.Generation,
            node.Weight,
            node.PriorityTier,
            node.MaxCapacity,
            Tags = node.Tags != null ? JsonSerializer.Serialize(node.Tags) : null,
            node.AsteriskVersion,
            node.StartupTime
        });
    }

    public override async ValueTask UnregisterNodeAsync(
        string nodeId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM cluster_nodes WHERE node_id = @nodeId", new { nodeId });
    }

    public override async ValueTask<IReadOnlyList<ClusterNode>> GetNodesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<NodeRow>("SELECT * FROM cluster_nodes");
        return rows.Select(MapToClusterNode).ToList();
    }

    public override async ValueTask UpdateNodeStateAsync(
        string nodeId, NodeState state, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE cluster_nodes SET state = @state WHERE node_id = @nodeId",
            new { nodeId, state = state.ToString() });
    }

    // ── Distributed Locking ──────────────────────────────────────

    public override async ValueTask<bool> TryAcquireLockAsync(
        string resource, string owner, TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync("""
            INSERT INTO cluster_locks (resource, owner, expires_at)
            VALUES (@resource, @owner, @expiresAt)
            ON CONFLICT (resource) DO UPDATE
                SET owner = EXCLUDED.owner, expires_at = EXCLUDED.expires_at
                WHERE cluster_locks.expires_at < now() OR cluster_locks.owner = EXCLUDED.owner
            """, new { resource, owner, expiresAt = DateTimeOffset.UtcNow + expiry });
        return affected > 0;
    }

    public override async ValueTask ReleaseLockAsync(
        string resource, string owner, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "DELETE FROM cluster_locks WHERE resource = @resource AND owner = @owner",
            new { resource, owner });
    }

    // ── Row mapping ──────────────────────────────────────────────

    private static ClusterNode MapToClusterNode(NodeRow r) => new()
    {
        NodeId = r.node_id,
        AmiOptions = new Sdk.Ami.AmiConnectionOptions
        {
            Hostname = r.ami_hostname,
            Port = r.ami_port,
            Username = r.ami_username,
            Password = r.ami_password
        },
        State = Enum.TryParse<NodeState>(r.state, out var s) ? s : NodeState.Unknown,
        OwnerInstanceId = r.owner_instance,
        Generation = r.generation,
        Weight = r.weight,
        PriorityTier = r.priority_tier,
        MaxCapacity = r.max_capacity,
        Tags = r.tags != null
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(r.tags)
            : null,
        AsteriskVersion = r.asterisk_version,
        StartupTime = r.startup_time
    };

    private sealed record NodeRow(
        string node_id, string ami_hostname, int ami_port,
        string ami_username, string ami_password, string state,
        string? owner_instance, long generation, double weight,
        int priority_tier, int max_capacity, string? tags,
        string? asterisk_version, DateTimeOffset? startup_time);
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Sdk.Pro.Cluster/`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Sdk.Pro.Cluster/Transport/PostgresClusterTransport.cs
git commit -m "feat: add PostgresClusterTransport with node registry and distributed locking"
```

---

### Task 3: PostgresClusterTransport — Heartbeat, Sessions, Drains, Generations

**Files:**
- Modify: `src/Asterisk.Sdk.Pro.Cluster/Transport/PostgresClusterTransport.cs`

- [ ] **Step 1: Add heartbeat methods**

Append to `PostgresClusterTransport` class:

```csharp
    // ── Instance Heartbeat ───────────────────────────────────────

    public override async ValueTask HeartbeatAsync(
        InstanceInfo instance, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            INSERT INTO cluster_instances (instance_id, owned_node_ids, total_channels, total_agents, last_seen, expires_at)
            VALUES (@InstanceId, @OwnedNodeIds::jsonb, @TotalChannels, @TotalAgents, @LastSeen, @ExpiresAt)
            ON CONFLICT (instance_id) DO UPDATE SET
                owned_node_ids = EXCLUDED.owned_node_ids, total_channels = EXCLUDED.total_channels,
                total_agents = EXCLUDED.total_agents, last_seen = EXCLUDED.last_seen,
                expires_at = EXCLUDED.expires_at
            """, new
        {
            instance.InstanceId,
            OwnedNodeIds = JsonSerializer.Serialize(instance.OwnedNodeIds),
            instance.TotalChannels,
            instance.TotalAgents,
            instance.LastSeen,
            ExpiresAt = instance.LastSeen + ttl
        });
    }

    public override async ValueTask<IReadOnlyList<InstanceInfo>> GetLiveInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<InstanceRow>(
            "SELECT * FROM cluster_instances WHERE expires_at > now()");
        return rows.Select(r => new InstanceInfo
        {
            InstanceId = r.instance_id,
            LastSeen = r.last_seen,
            OwnedNodeIds = JsonSerializer.Deserialize<List<string>>(r.owned_node_ids) ?? [],
            TotalChannels = r.total_channels,
            TotalAgents = r.total_agents
        }).ToList();
    }
```

- [ ] **Step 2: Add session snapshot methods**

```csharp
    // ── Session Snapshots ────────────────────────────────────────

    public override async ValueTask SaveSessionSnapshotAsync(
        SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            INSERT INTO cluster_session_snapshots
                (server_id, linked_id, session_id, state, direction, created_at,
                 queue_name, agent_id, bridge_id, hold_time, metadata)
            VALUES (@ServerId, @LinkedId, @SessionId, @State, @Direction, @CreatedAt,
                    @QueueName, @AgentId, @BridgeId, @HoldTime, @Metadata::jsonb)
            ON CONFLICT (server_id, linked_id) DO UPDATE SET
                session_id = EXCLUDED.session_id, state = EXCLUDED.state,
                direction = EXCLUDED.direction, queue_name = EXCLUDED.queue_name,
                agent_id = EXCLUDED.agent_id, bridge_id = EXCLUDED.bridge_id,
                hold_time = EXCLUDED.hold_time, metadata = EXCLUDED.metadata
            """, new
        {
            snapshot.ServerId,
            snapshot.LinkedId,
            snapshot.SessionId,
            snapshot.State,
            snapshot.Direction,
            snapshot.CreatedAt,
            snapshot.QueueName,
            snapshot.AgentId,
            snapshot.BridgeId,
            HoldTime = snapshot.AccumulatedHoldTime,
            Metadata = snapshot.Metadata != null
                ? JsonSerializer.Serialize(snapshot.Metadata)
                : null
        });
    }

    public override async ValueTask<SessionSnapshot?> GetSessionSnapshotAsync(
        string serverId, string linkedId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<SnapshotRow>(
            "SELECT * FROM cluster_session_snapshots WHERE server_id = @serverId AND linked_id = @linkedId",
            new { serverId, linkedId });
        return row != null ? MapToSnapshot(row) : null;
    }

    public override async ValueTask<IReadOnlyList<SessionSnapshot>> GetSessionSnapshotsForServerAsync(
        string serverId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<SnapshotRow>(
            "SELECT * FROM cluster_session_snapshots WHERE server_id = @serverId",
            new { serverId });
        return rows.Select(MapToSnapshot).ToList();
    }

    public override async ValueTask RemoveSessionSnapshotAsync(
        string serverId, string linkedId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "DELETE FROM cluster_session_snapshots WHERE server_id = @serverId AND linked_id = @linkedId",
            new { serverId, linkedId });
    }
```

- [ ] **Step 3: Add drain state methods**

```csharp
    // ── Drain State ──────────────────────────────────────────────

    public override async ValueTask SaveDrainStateAsync(
        DrainStatus status, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            INSERT INTO cluster_drain_states
                (node_id, state, started_at, deadline, initial_call_count,
                 remaining_call_count, naturally_completed, force_disconnected)
            VALUES (@NodeId, @State, @StartedAt, @Deadline, @InitialCallCount,
                    @RemainingCallCount, @NaturallyCompleted, @ForceDisconnected)
            ON CONFLICT (node_id) DO UPDATE SET
                state = EXCLUDED.state, deadline = EXCLUDED.deadline,
                remaining_call_count = EXCLUDED.remaining_call_count,
                naturally_completed = EXCLUDED.naturally_completed,
                force_disconnected = EXCLUDED.force_disconnected
            """, new
        {
            status.NodeId,
            State = status.State.ToString(),
            status.StartedAt,
            status.Deadline,
            status.InitialCallCount,
            status.RemainingCallCount,
            status.NaturallyCompleted,
            status.ForceDisconnected
        });
    }

    public override async ValueTask<DrainStatus?> GetDrainStateAsync(
        string nodeId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<DrainRow>(
            "SELECT * FROM cluster_drain_states WHERE node_id = @nodeId",
            new { nodeId });
        if (row is null) return null;
        return new DrainStatus(
            row.node_id,
            Enum.TryParse<DrainState>(row.state, out var ds) ? ds : DrainState.Idle,
            row.started_at, row.deadline, row.initial_call_count,
            row.remaining_call_count, row.naturally_completed,
            row.force_disconnected, null);
    }

    public override async ValueTask RemoveDrainStateAsync(
        string nodeId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "DELETE FROM cluster_drain_states WHERE node_id = @nodeId",
            new { nodeId });
    }
```

- [ ] **Step 4: Add generation counter**

```csharp
    // ── Generation Counter ───────────────────────────────────────

    public override async ValueTask<long> IncrementGenerationAsync(
        string nodeId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var gen = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO cluster_generations (node_id, generation)
            VALUES (@nodeId, 1)
            ON CONFLICT (node_id) DO UPDATE SET generation = cluster_generations.generation + 1
            RETURNING generation
            """, new { nodeId });
        return gen;
    }
```

- [ ] **Step 5: Add row mapping records**

Append at the bottom of the class:

```csharp
    private static SessionSnapshot MapToSnapshot(SnapshotRow r) => new()
    {
        ServerId = r.server_id,
        LinkedId = r.linked_id,
        SessionId = r.session_id,
        State = r.state,
        Direction = r.direction,
        CreatedAt = r.created_at,
        QueueName = r.queue_name,
        AgentId = r.agent_id,
        BridgeId = r.bridge_id,
        AccumulatedHoldTime = r.hold_time ?? TimeSpan.Zero,
        Metadata = r.metadata != null
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(r.metadata)
            : null
    };

    private sealed record InstanceRow(
        string instance_id, string owned_node_ids,
        int total_channels, int total_agents,
        DateTimeOffset last_seen, DateTimeOffset expires_at);

    private sealed record SnapshotRow(
        string server_id, string linked_id, string session_id,
        string state, string direction, DateTimeOffset created_at,
        string? queue_name, string? agent_id, string? bridge_id,
        TimeSpan? hold_time, string? metadata);

    private sealed record DrainRow(
        string node_id, string state,
        DateTimeOffset started_at, DateTimeOffset deadline,
        int initial_call_count, int remaining_call_count,
        int naturally_completed, int force_disconnected);
```

- [ ] **Step 6: Verify build**

Run: `dotnet build src/Asterisk.Sdk.Pro.Cluster/`
Expected: Build succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/Asterisk.Sdk.Pro.Cluster/Transport/PostgresClusterTransport.cs
git commit -m "feat: add heartbeat, sessions, drains, generations to PostgresClusterTransport"
```

---

### Task 4: PostgresClusterTransport — Pub/Sub via LISTEN/NOTIFY

**Files:**
- Modify: `src/Asterisk.Sdk.Pro.Cluster/Transport/PostgresClusterTransport.cs`

- [ ] **Step 1: Add Publish method**

```csharp
    // ── Pub/Sub ──────────────────────────────────────────────────

    public override async ValueTask PublishAsync(
        ClusterEvent clusterEvent, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(clusterEvent, clusterEvent.GetType());
        var typeName = clusterEvent.GetType().Name;
        var payload = JsonSerializer.Serialize(new { Type = typeName, Data = json });

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"NOTIFY cluster_events, '{payload.Replace("'", "''")}'";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
```

- [ ] **Step 2: Add Subscribe method with LISTEN loop**

```csharp
    public override async IAsyncEnumerable<ClusterEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ClusterEvent>();
        lock (_subscriberLock)
        {
            _subscribers.Add(channel);
        }

        StartListenerIfNeeded();

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (_subscriberLock)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    private void StartListenerIfNeeded()
    {
        if (_listenerTask is { IsCompleted: false }) return;

        _listenerCts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenLoopAsync(_listenerCts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "LISTEN cluster_events";
                await cmd.ExecuteNonQueryAsync(ct);

                conn.Notification += (_, e) =>
                {
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<JsonElement>(e.Payload);
                        var typeName = envelope.GetProperty("Type").GetString();
                        var data = envelope.GetProperty("Data").GetString();
                        var evt = DeserializeEvent(typeName!, data!);
                        if (evt is null) return;

                        lock (_subscriberLock)
                        {
                            foreach (var sub in _subscribers)
                                sub.Writer.TryWrite(evt);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogNotificationError(ex);
                    }
                };

                while (!ct.IsCancellationRequested)
                {
                    await conn.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogListenerError(ex);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private static ClusterEvent? DeserializeEvent(string typeName, string json) => typeName switch
    {
        nameof(NodeStateChangedEvent) => JsonSerializer.Deserialize<NodeStateChangedEvent>(json),
        nameof(InstanceLostEvent) => JsonSerializer.Deserialize<InstanceLostEvent>(json),
        nameof(DrainProgressEvent) => JsonSerializer.Deserialize<DrainProgressEvent>(json),
        nameof(DrainCompletedEvent) => JsonSerializer.Deserialize<DrainCompletedEvent>(json),
        nameof(InstanceDepartingEvent) => JsonSerializer.Deserialize<InstanceDepartingEvent>(json),
        _ => null
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing cluster notification")]
    private partial void LogNotificationError(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cluster LISTEN loop error, reconnecting in 5s")]
    private partial void LogListenerError(Exception ex);
```

- [ ] **Step 3: Add DisposeAsync override**

```csharp
    public override async ValueTask DisposeAsync()
    {
        _listenerCts?.Cancel();
        if (_listenerTask is not null)
        {
            try { await _listenerTask; } catch (OperationCanceledException) { }
        }
        _listenerCts?.Dispose();

        lock (_subscriberLock)
        {
            foreach (var sub in _subscribers)
                sub.Writer.TryComplete();
            _subscribers.Clear();
        }
    }
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/Asterisk.Sdk.Pro.Cluster/`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Sdk.Pro.Cluster/Transport/PostgresClusterTransport.cs
git commit -m "feat: add LISTEN/NOTIFY pub/sub to PostgresClusterTransport"
```

---

### Task 5: DI Extension + UpdateNodeAsync

**Files:**
- Modify: `src/Asterisk.Sdk.Pro.Cluster/DependencyInjection/ClusterServiceCollectionExtensions.cs`
- Modify: `src/Asterisk.Sdk.Pro.Cluster/Transport/ClusterTransportBase.cs`
- Modify: `src/Asterisk.Sdk.Pro.Cluster/Transport/InMemoryClusterTransport.cs`
- Modify: `src/Asterisk.Sdk.Pro.Cluster/Transport/PostgresClusterTransport.cs`
- Modify: `src/Asterisk.Sdk.Pro.Cluster/ClusterManager.cs`
- Create: `src/Asterisk.Sdk.Pro.Cluster/Transport/NodeUpdate.cs`

- [ ] **Step 1: Create NodeUpdate record**

```csharp
// src/Asterisk.Sdk.Pro.Cluster/Transport/NodeUpdate.cs
namespace Asterisk.Sdk.Pro.Cluster.Transport;

public sealed record NodeUpdate(
    double? Weight = null,
    int? PriorityTier = null,
    int? MaxCapacity = null,
    IReadOnlyDictionary<string, string>? Tags = null);
```

- [ ] **Step 2: Add UpdateNodeAsync to ClusterTransportBase**

Add after `UpdateNodeStateAsync`:

```csharp
public abstract ValueTask UpdateNodeAsync(
    string nodeId, NodeUpdate update, CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Implement in InMemoryClusterTransport**

```csharp
public override ValueTask UpdateNodeAsync(
    string nodeId, NodeUpdate update, CancellationToken cancellationToken = default)
{
    if (_nodes.TryGetValue(nodeId, out var node))
    {
        if (update.Weight.HasValue) node.Weight = update.Weight.Value;
        if (update.PriorityTier.HasValue) node.PriorityTier = update.PriorityTier.Value;
        if (update.MaxCapacity.HasValue) node.MaxCapacity = update.MaxCapacity.Value;
        if (update.Tags is not null) node.Tags = update.Tags;
    }
    return ValueTask.CompletedTask;
}
```

- [ ] **Step 4: Implement in PostgresClusterTransport**

```csharp
public override async ValueTask UpdateNodeAsync(
    string nodeId, NodeUpdate update, CancellationToken cancellationToken = default)
{
    var setClauses = new List<string>();
    var parameters = new DynamicParameters();
    parameters.Add("nodeId", nodeId);

    if (update.Weight.HasValue)
    {
        setClauses.Add("weight = @weight");
        parameters.Add("weight", update.Weight.Value);
    }
    if (update.PriorityTier.HasValue)
    {
        setClauses.Add("priority_tier = @priorityTier");
        parameters.Add("priorityTier", update.PriorityTier.Value);
    }
    if (update.MaxCapacity.HasValue)
    {
        setClauses.Add("max_capacity = @maxCapacity");
        parameters.Add("maxCapacity", update.MaxCapacity.Value);
    }
    if (update.Tags is not null)
    {
        setClauses.Add("tags = @tags::jsonb");
        parameters.Add("tags", JsonSerializer.Serialize(update.Tags));
    }

    if (setClauses.Count == 0) return;

    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.ExecuteAsync(
        $"UPDATE cluster_nodes SET {string.Join(", ", setClauses)} WHERE node_id = @nodeId",
        parameters);
}
```

- [ ] **Step 5: Add UpdateNodeAsync to ClusterManager**

Add after `RemoveNodeAsync`:

```csharp
public async ValueTask UpdateNodeAsync(
    string nodeId,
    double? weight = null,
    int? priorityTier = null,
    int? maxCapacity = null,
    IReadOnlyDictionary<string, string>? tags = null,
    CancellationToken cancellationToken = default)
{
    var node = Registry.GetNode(nodeId)
        ?? throw new InvalidOperationException($"Node '{nodeId}' not found");

    lock (node.SyncRoot)
    {
        if (weight.HasValue) node.Weight = weight.Value;
        if (priorityTier.HasValue) node.PriorityTier = priorityTier.Value;
        if (maxCapacity.HasValue) node.MaxCapacity = maxCapacity.Value;
        if (tags is not null) node.Tags = tags;
    }

    await _transport.UpdateNodeAsync(nodeId,
        new NodeUpdate(weight, priorityTier, maxCapacity, tags), cancellationToken);
}
```

- [ ] **Step 6: Add DI extension method**

Add to `ClusterServiceCollectionExtensions.cs`:

```csharp
public static IServiceCollection UsePostgresClusterTransport(
    this IServiceCollection services, string connectionString)
{
    services.RemoveAll<ClusterTransportBase>();
    services.AddSingleton<ClusterTransportBase>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<PostgresClusterTransport>>();
        var transport = new PostgresClusterTransport(connectionString, logger);
        transport.EnsureSchemaAsync().GetAwaiter().GetResult();
        return transport;
    });
    return services;
}
```

Add required using:
```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;
using Asterisk.Sdk.Pro.Cluster.Transport;
```

- [ ] **Step 7: Verify build**

Run: `dotnet build src/Asterisk.Sdk.Pro.Cluster/`
Expected: Build succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/Asterisk.Sdk.Pro.Cluster/
git commit -m "feat: add UpdateNodeAsync, NodeUpdate, UsePostgresClusterTransport DI extension"
```

---

### Task 6: Tests

**Files:**
- Create: `tests/Asterisk.Sdk.Pro.Cluster.Tests/PostgresClusterTransportTests.cs` (or add to existing test project)

- [ ] **Step 1: Write tests for InMemoryClusterTransport UpdateNodeAsync**

Since PostgresClusterTransport requires a real database, test the UpdateNodeAsync behavior via InMemoryClusterTransport and test the ClusterManager.UpdateNodeAsync integration:

```csharp
[Fact]
public async Task UpdateNodeAsync_ShouldUpdateWeight_WhenWeightProvided()
{
    // Arrange
    var transport = new InMemoryClusterTransport();
    var node = new ClusterNode
    {
        NodeId = "test-node",
        AmiOptions = new AmiConnectionOptions { Hostname = "localhost", Username = "admin", Password = "secret" },
        Weight = 1.0
    };
    await transport.RegisterNodeAsync(node);

    // Act
    await transport.UpdateNodeAsync("test-node", new NodeUpdate(Weight: 2.5));

    // Assert
    var nodes = await transport.GetNodesAsync();
    nodes.Should().ContainSingle(n => n.NodeId == "test-node" && n.Weight == 2.5);
}

[Fact]
public async Task UpdateNodeAsync_ShouldNotChangOtherFields_WhenPartialUpdate()
{
    // Arrange
    var transport = new InMemoryClusterTransport();
    var node = new ClusterNode
    {
        NodeId = "test-node",
        AmiOptions = new AmiConnectionOptions { Hostname = "localhost", Username = "admin", Password = "secret" },
        Weight = 1.0,
        PriorityTier = 5,
        MaxCapacity = 200
    };
    await transport.RegisterNodeAsync(node);

    // Act
    await transport.UpdateNodeAsync("test-node", new NodeUpdate(Weight: 3.0));

    // Assert
    var nodes = await transport.GetNodesAsync();
    var updated = nodes.Single();
    updated.PriorityTier.Should().Be(5);
    updated.MaxCapacity.Should().Be(200);
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Asterisk.Sdk.Pro.Cluster.Tests/ -v q`
Expected: All tests pass.

- [ ] **Step 3: Pack and publish to local feed**

```bash
cd /media/Data/Source/Verbara/Asterisk.Sdk.Pro
dotnet pack src/Asterisk.Sdk.Pro.Cluster/ -c Release -o /media/Data/Source/Verbara/local-nuget-feed/
rm -rf ~/.nuget/packages/asterisk.sdk.pro.cluster*/
```

- [ ] **Step 4: Commit**

```bash
git add tests/Asterisk.Sdk.Pro.Cluster.Tests/
git commit -m "test: add UpdateNodeAsync tests for InMemoryClusterTransport"
```
