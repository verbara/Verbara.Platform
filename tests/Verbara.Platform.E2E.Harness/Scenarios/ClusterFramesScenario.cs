using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Verbara.Platform.E2E.Harness.Audit;
using Verbara.Platform.E2E.Harness.Auth;
using Verbara.Platform.E2E.Harness.Config;
using Verbara.Platform.E2E.Harness.Hub;
using Verbara.Platform.E2E.Harness.Trigger;
using Verbara.Platform.Realtime.Contracts.Dtos;

namespace Verbara.Platform.E2E.Harness.Scenarios;

/// <summary>One REST step of the cluster-frames fixture or workload, with the status it answered.</summary>
internal sealed record RestStep(
    string Name,
    string Method,
    string Path,
    int Status,
    string Expected,
    bool Ok,
    string? ResponseBody,
    DateTimeOffset At);

/// <summary>One read of <c>GET /api/v1/management/cluster/nodes</c>: node id to <c>state</c>, and the raw body.</summary>
internal sealed record NodeRead(
    string Label,
    DateTimeOffset At,
    int Status,
    IReadOnlyDictionary<string, string> States,
    string RawBody);

/// <summary>What the evaluator of <see cref="ClusterFramesScenario"/> judges (also built by the offline self-check).</summary>
internal sealed record ClusterFramesRecord(
    int ExpectedAgentFrames,
    int AgentFramesInLiveness,
    int AgentForwardedRowsInLiveness,
    IReadOnlyList<ClusterFrame> ClusterFrames,
    IReadOnlyList<ClusterFrame> LivenessClientClusterFrames,
    int ClusterAuditRows,
    IReadOnlyList<RestStep> Steps,
    IReadOnlyList<string> RequiredNodes,
    string DrainedNode,
    NodeRead? FixtureRead,
    NodeRead? PreDrainRead,
    bool DrainPollSawDraining,
    bool CancelPollLeftDraining,
    string HostClientStateAtEnd);

/// <summary>The five rules of tasks.md 1.1 (amended 2026-11-21) applied to one record.</summary>
internal sealed record ClusterFramesVerdict(
    bool Passed,
    bool LivenessPassed,
    bool NoClusterFramesPassed,
    bool NoClusterAuditRowsPassed,
    bool RestStepsPassed,
    bool HostClientConnectedPassed,
    IReadOnlyList<string> Failures);

/// <summary>Result of the cluster-frames scenario (tasks 1.1 and 4.1 of platform-drops-the-cluster-bridge).</summary>
internal sealed record ClusterFramesReport(
    string ScenarioName,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? FixtureError,
    ClusterFramesVerdict Verdict,
    ClusterFramesRecord Record,
    string Tenant,
    string AdminTenant,
    string LivenessAdminTenant,
    DateTimeOffset? LivenessWindowStart,
    DateTimeOffset? LivenessWindowEnd,
    DateTimeOffset? ClusterWindowStart,
    DateTimeOffset? ClusterWindowEnd,
    int AgentFramesWholeRun,
    int AgentForwardedRowsInLivenessAllTenants,
    IReadOnlyDictionary<string, int> ClusterAuditRowsByEventTypeAndOutcome,
    IReadOnlyList<NodeRead> NodeReads,
    IReadOnlyList<string> BridgeMetricsBeforeClusterWindow,
    IReadOnlyList<string> BridgeMetricsAfterClusterWindow,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RelayOutcomePage> AuditPages);

/// <summary>
/// The lab cell of <c>platform-drops-the-cluster-bridge</c> (tasks.md 1.1 and 4.1; design D6). Two hub clients
/// connect first and stay to the end: the <b>host client</b>, signed in as the host tenant's Admin (the
/// <c>/admin/cluster</c> listener, subject of rules 2-5), and the <b>liveness client</b>, signed in as the Admin of a
/// Customer tenant (<c>acme</c>), because operational endpoints refuse a Platform tenant (409
/// <c>tenant-type-mismatch</c>), so no host-tenant agent can exist (amendment approved by the owner 2026-11-21).
/// Fixture: the second Asterisk node is registered through <c>POST /api/v1/management/cluster/nodes</c> and both
/// nodes must read <c>"healthy"</c>. Liveness window: K agent state changes of the Customer tenant's agent. Cluster window: both nodes re-read <c>"healthy"</c>, the drained node
/// is drained (<c>{"gracePeriodSeconds":120}</c>, 202), seen <c>"draining"</c>, the drain cancelled (2xx), seen to
/// leave <c>"draining"</c>, and the window closes 10 s later.
/// </summary>
/// <remarks>
/// Pass (each build): (1) the liveness client holds exactly K <c>OnAgentStateChanged</c> frames and the audit holds K
/// Forwarded <c>agent.state_changed</c> rows of the agent's tenant in the liveness window; (2) both clients hold 0
/// <c>OnClusterNodeStateChanged</c> frames in the whole run; (3) the audit holds 0 rows of any outcome for
/// <c>cluster.node.state.changed</c> (the whole ring of every pod); (4) every REST step answered as declared, both
/// nodes read <c>"healthy"</c> in the fixture read and in the pre-drain read, and the drain poll saw
/// <c>"draining"</c>; (5) the host client reads <c>Connected</c> at the end of the run. A refused step or a node that never reaches <c>"healthy"</c> is a fixture error (exit 2): fix
/// and rerun, never count it. Environment beyond <see cref="HarnessConfig"/>: <c>HARNESS_CLUSTER_NODE_ID</c>
/// (lab-node-2), <c>HARNESS_CLUSTER_NODE_AMI_HOST</c> (dut2), <c>HARNESS_CLUSTER_NODE_AMI_PORT</c> (5038),
/// <c>HARNESS_CLUSTER_NODE_AMI_USER</c> (platform), <c>HARNESS_CLUSTER_NODE_AMI_PASSWORD</c> (required),
/// <c>HARNESS_CLUSTER_DRAIN_NODE</c> (primary), <c>HARNESS_CLUSTER_GRACE_SEC</c> (120),
/// <c>HARNESS_LIVENESS_ADMIN_EMAIL</c> and <c>HARNESS_LIVENESS_ADMIN_PASSWORD</c> (required: the liveness client),
/// <c>HARNESS_LIVENESS_ADMIN_TENANT</c> (default <c>HARNESS_TENANT</c>). The agent (<c>HARNESS_AGENT_EMAIL</c>) belongs
/// to <c>HARNESS_TENANT</c>; the host Admin is <c>HARNESS_PLATFORMADMIN_*</c> in <c>HARNESS_ADMIN_TENANT</c>.
/// </remarks>
internal static class ClusterFramesScenario
{
    public const string Name = "cluster-frames";
    public const string ClusterEventType = "cluster.node.state.changed";
    public const string ClusterDrainEventType = "cluster.node.drain.progress";
    public const string CoreAgentEventType = "agent.state_changed";
    private const string NodesPath = "/api/v1/management/cluster/nodes";

    public static async Task<ClusterFramesReport> RunAsync(HarnessConfig config, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var nodeId = Env("HARNESS_CLUSTER_NODE_ID", "lab-node-2");
        var amiHost = Env("HARNESS_CLUSTER_NODE_AMI_HOST", "dut2");
        var amiPort = int.Parse(Env("HARNESS_CLUSTER_NODE_AMI_PORT", "5038"), System.Globalization.CultureInfo.InvariantCulture);
        var amiUser = Env("HARNESS_CLUSTER_NODE_AMI_USER", "platform");
        var amiPassword = Environment.GetEnvironmentVariable("HARNESS_CLUSTER_NODE_AMI_PASSWORD")
            ?? throw new InvalidOperationException("HARNESS_CLUSTER_NODE_AMI_PASSWORD is required for scenario 'cluster-frames'.");
        var drainNode = Env("HARNESS_CLUSTER_DRAIN_NODE", "primary");
        var graceSec = int.Parse(Env("HARNESS_CLUSTER_GRACE_SEC", "120"), System.Globalization.CultureInfo.InvariantCulture);
        string[] required = [drainNode, nodeId];
        var livenessEmail = Environment.GetEnvironmentVariable("HARNESS_LIVENESS_ADMIN_EMAIL")
            ?? throw new InvalidOperationException("HARNESS_LIVENESS_ADMIN_EMAIL is required for scenario 'cluster-frames'.");
        var livenessPassword = Environment.GetEnvironmentVariable("HARNESS_LIVENESS_ADMIN_PASSWORD")
            ?? throw new InvalidOperationException("HARNESS_LIVENESS_ADMIN_PASSWORD is required for scenario 'cluster-frames'.");
        var livenessTenant = Env("HARNESS_LIVENESS_ADMIN_TENANT", config.Tenant);

        var steps = new List<RestStep>();
        var reads = new List<NodeRead>();
        var warnings = new List<string>();
        NodeRead? fixtureRead = null, preDrainRead = null;
        bool sawDraining = false, leftDraining = false;
        DateTimeOffset? livenessStart = null, livenessEnd = null, clusterStart = null, clusterEnd = null;
        IReadOnlyList<string> metricsBefore = [], metricsAfter = [];
        string? fixtureError = null;
        var agentFramesInLiveness = 0;

        using var auth = new PlatformAuthClient(config.ApiBaseUrl);
        Log($"Logging in as the host Admin '{config.PlatformAdminEmail}' (tenant '{config.AdminTenant}')...");
        var adminToken = await auth.LoginAsync(config.PlatformAdminEmail, config.PlatformAdminPassword, config.AdminTenant, ct).ConfigureAwait(false);
        Log($"Logging in as the agent '{config.Email}' (tenant '{config.Tenant}')...");
        var agentToken = await auth.LoginAsync(config.Email, config.Password, config.Tenant, ct).ConfigureAwait(false);
        Log($"Logging in as the liveness Admin '{livenessEmail}' (tenant '{livenessTenant}')...");
        var livenessToken = await auth.LoginAsync(livenessEmail, livenessPassword, livenessTenant, ct).ConfigureAwait(false);

        using var api = new HttpClient { BaseAddress = new Uri(config.ApiBaseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        api.DefaultRequestHeaders.Add("X-Tenant-Id", config.AdminTenant);

        // Two hub clients, connected before the fixture and kept to the end: the host Admin (rules 2-5) and the
        // Customer tenant's Admin (rule 1, and rule 2).
        Log($"Connecting the host client (host Admin) and the liveness client ('{livenessEmail}') to {config.RealtimeHubUrl}...");
        await using var pool = new SignalRClientPool(config.RealtimeHubUrl, adminToken);
        await pool.ConnectAsync(1, ct).ConfigureAwait(false);
        await using var livenessPool = new SignalRClientPool(config.RealtimeHubUrl, livenessToken);
        await livenessPool.ConnectAsync(1, ct).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); // fence-allow: SETTLE — let the SignalR tenant-group join settle against the live hub

        // Fixture: register the second node, then both nodes "healthy" (at most 60 s).
        var body = JsonSerializer.Serialize(new { nodeId, amiHostname = amiHost, amiPort, amiUsername = amiUser, amiPassword });
        var register = await SendAsync(api, "register-node", HttpMethod.Post, NodesPath, body, "201", s => s == 201 || s == 202, ct).ConfigureAwait(false);
        steps.Add(register);
        if (register.Ok)
        {
            fixtureRead = await PollNodesAsync(api, "fixture", TimeSpan.FromSeconds(60), reads,
                r => required.All(n => r.States.TryGetValue(n, out var s) && s == "healthy"), ct).ConfigureAwait(false);
            if (!required.All(n => fixtureRead.States.TryGetValue(n, out var s) && s == "healthy"))
            {
                fixtureError = $"Fixture: within 60 s the nodes read {Describe(fixtureRead)}; both {string.Join(" and ", required)} must read \"healthy\".";
            }
            else if (register.Status == 202 && !fixtureRead.States.ContainsKey(nodeId))
            {
                fixtureError = $"Fixture: the registration answered 202 and the next read does not list '{nodeId}'.";
            }
        }
        else
        {
            fixtureError = $"Fixture: {register.Method} {register.Path} answered {register.Status}, expected 201.";
        }

        if (fixtureError is null)
        {
            // (a) Liveness window: K agent state changes of the host-tenant agent.
            pool.ResetAgentCounts();
            livenessPool.ResetAgentCounts();
            livenessStart = DateTimeOffset.UtcNow.AddMilliseconds(-500);
            Log($"Liveness window opens at {livenessStart:O}: {config.EventCount} agent state change(s).");
            using (var trigger = new AgentStateTrigger(config.ApiBaseUrl, agentToken, config.Tenant))
            {
                await trigger.EmitAsync(config.EventCount, ct).ConfigureAwait(false);
            }

            await Task.Delay(config.SettleDelay, ct).ConfigureAwait(false); // fence-allow: SETTLE — settle before closing the liveness window
            livenessEnd = DateTimeOffset.UtcNow;
            agentFramesInLiveness = livenessPool.ReceivedCounts[0];

            // (b) Cluster window.
            clusterStart = DateTimeOffset.UtcNow;
            metricsBefore = await BridgeMetricsAsync(config.ApiBaseUrl, ct).ConfigureAwait(false);
            Log($"Cluster window opens at {clusterStart:O}.");
            preDrainRead = await ReadNodesAsync(api, "pre-drain", ct).ConfigureAwait(false);
            reads.Add(preDrainRead);
            if (!required.All(n => preDrainRead.States.TryGetValue(n, out var s) && s == "healthy"))
            {
                fixtureError = $"Pre-drain read: {Describe(preDrainRead)}; both nodes must read \"healthy\".";
            }
            else
            {
                var drain = await SendAsync(api, "drain", HttpMethod.Post, $"{NodesPath}/{drainNode}/drain",
                    JsonSerializer.Serialize(new { gracePeriodSeconds = graceSec }), "202", s => s == 202, ct).ConfigureAwait(false);
                steps.Add(drain);
                if (!drain.Ok)
                {
                    fixtureError = $"Drain answered {drain.Status} ({drain.ResponseBody}), expected 202.";
                }
                else
                {
                    var draining = await PollNodesAsync(api, "drain-poll", TimeSpan.FromSeconds(30), reads,
                        r => r.States.TryGetValue(drainNode, out var s) && s == "draining", ct).ConfigureAwait(false);
                    sawDraining = draining.States.TryGetValue(drainNode, out var ds) && ds == "draining";
                    var cancel = await SendAsync(api, "cancel-drain", HttpMethod.Delete, $"{NodesPath}/{drainNode}/drain",
                        null, "2xx", s => s is >= 200 and < 300, ct).ConfigureAwait(false);
                    steps.Add(cancel);
                    if (cancel.Ok)
                    {
                        var after = await PollNodesAsync(api, "cancel-poll", TimeSpan.FromSeconds(30), reads,
                            r => r.States.TryGetValue(drainNode, out var s) && s != "draining", ct).ConfigureAwait(false);
                        leftDraining = after.States.TryGetValue(drainNode, out var cs) && cs != "draining";
                    }

                    if (!sawDraining)
                    {
                        fixtureError = $"The drain poll never saw '{drainNode}' \"draining\" within 30 s.";
                    }
                    else if (!cancel.Ok)
                    {
                        fixtureError = $"Cancel answered {cancel.Status}, expected 2xx.";
                    }
                    else if (!leftDraining)
                    {
                        fixtureError = $"'{drainNode}' still read \"draining\" 30 s after the cancel.";
                    }
                }
            }

            Log("Closing the cluster window in 10 s.");
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); // fence-allow: SETTLE — the declared 10 s tail of the cluster window
            clusterEnd = DateTimeOffset.UtcNow;
            metricsAfter = await BridgeMetricsAsync(config.ApiBaseUrl, ct).ConfigureAwait(false);
        }

        // The host client's connection state at the end of the run (rule 5): the only liveness that client can show.
        var hostState = pool.ConnectionStates.Count == 1 ? pool.ConnectionStates[0].ToString() : "absent";
        Log($"Host client state at the end of the run: {hostState}.");

        // Audit: the whole ring of every pod (since = none), so a cluster row of any outcome at any time counts. Read
        // with the Customer Admin's token, as C10's scenarios read it (the ring is per pod and not tenant-filtered).
        var auditClients = config.AuditBaseUrls.Select(url => new AuditClient(url, livenessToken)).ToList();
        RelayOutcomePage[] pages;
        try
        {
            pages = await Task.WhenAll(auditClients.Select(c => c.FetchAsync(null, limit: 100_000, ct))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var c in auditClients)
            {
                c.Dispose();
            }
        }

        warnings.AddRange(MultiPodAuditAggregator.Compute(pages).EvictionWarnings);
        var entries = pages.SelectMany(p => p.Entries).ToList();
        bool InLiveness(RelayOutcomeEntry e) => livenessStart is { } a && livenessEnd is { } b && e.Ts >= a && e.Ts <= b;
        var agentRows = entries.Where(e => e.Outcome == RelayOutcomeKind.Forwarded
            && string.Equals(e.EventType, CoreAgentEventType, StringComparison.Ordinal) && InLiveness(e)).ToList();
        var clusterRows = entries.Where(e => string.Equals(e.EventType, ClusterEventType, StringComparison.Ordinal)).ToList();
        var clusterByKind = entries
            .Where(e => e.EventType.StartsWith("cluster.", StringComparison.Ordinal))
            .GroupBy(e => $"{e.EventType}|{e.Outcome}")
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var record = new ClusterFramesRecord(
            ExpectedAgentFrames: config.EventCount,
            AgentFramesInLiveness: agentFramesInLiveness,
            AgentForwardedRowsInLiveness: agentRows.Count(e => string.Equals(e.TenantId, config.Tenant, StringComparison.Ordinal)),
            ClusterFrames: pool.ClusterFrames[0],
            LivenessClientClusterFrames: livenessPool.ClusterFrames[0],
            ClusterAuditRows: clusterRows.Count,
            Steps: steps,
            RequiredNodes: required,
            DrainedNode: drainNode,
            FixtureRead: fixtureRead,
            PreDrainRead: preDrainRead,
            DrainPollSawDraining: sawDraining,
            CancelPollLeftDraining: leftDraining,
            HostClientStateAtEnd: hostState);
        var verdict = Evaluate(record);

        return new ClusterFramesReport(
            ScenarioName: Name,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow,
            FixtureError: fixtureError,
            Verdict: verdict,
            Record: record,
            Tenant: config.Tenant,
            AdminTenant: config.AdminTenant,
            LivenessAdminTenant: livenessTenant,
            LivenessWindowStart: livenessStart,
            LivenessWindowEnd: livenessEnd,
            ClusterWindowStart: clusterStart,
            ClusterWindowEnd: clusterEnd,
            AgentFramesWholeRun: livenessPool.ReceivedCounts[0],
            AgentForwardedRowsInLivenessAllTenants: agentRows.Count,
            ClusterAuditRowsByEventTypeAndOutcome: clusterByKind,
            NodeReads: reads,
            BridgeMetricsBeforeClusterWindow: metricsBefore,
            BridgeMetricsAfterClusterWindow: metricsAfter,
            Warnings: warnings,
            AuditPages: pages);
    }

    /// <summary>The five rules of tasks.md 1.1 (amended 2026-11-21) on one record.</summary>
    internal static ClusterFramesVerdict Evaluate(ClusterFramesRecord record)
    {
        var failures = new List<string>();

        // (1) Liveness: exactly K frames on the client and K Forwarded agent.state_changed rows in the window.
        var liveness = record.AgentFramesInLiveness == record.ExpectedAgentFrames
            && record.AgentForwardedRowsInLiveness == record.ExpectedAgentFrames;
        if (!liveness)
        {
            failures.Add($"Liveness: K = {record.ExpectedAgentFrames}; the client holds {record.AgentFramesInLiveness} " +
                         $"OnAgentStateChanged frame(s) and the audit {record.AgentForwardedRowsInLiveness} Forwarded {CoreAgentEventType} row(s) in the window.");
        }

        // (2) No cluster frame in the whole run, on either client.
        var noFrames = record.ClusterFrames.Count == 0 && record.LivenessClientClusterFrames.Count == 0;
        if (!noFrames)
        {
            failures.Add($"The host client holds {record.ClusterFrames.Count} and the liveness client " +
                         $"{record.LivenessClientClusterFrames.Count} OnClusterNodeStateChanged frame(s) — expected 0 on each.");
        }

        // (3) No audit row of any outcome for the cluster event type.
        var noRows = record.ClusterAuditRows == 0;
        if (!noRows)
        {
            failures.Add($"The audit holds {record.ClusterAuditRows} row(s) for {ClusterEventType} — expected 0 of any outcome.");
        }

        // (4) Every REST step as declared; both nodes healthy in the fixture read and the pre-drain read; draining seen.
        static bool AllHealthy(NodeRead? read, IReadOnlyList<string> nodes) =>
            read is not null && nodes.Count > 0 && nodes.All(n => read.States.TryGetValue(n, out var s) && s == "healthy");
        string[] declaredSteps = ["register-node", "drain", "cancel-drain"];
        var stepsOk = declaredSteps.All(name => record.Steps.Any(s => s.Name == name))
            && record.Steps.All(s => s.Ok);
        var rest = stepsOk
            && AllHealthy(record.FixtureRead, record.RequiredNodes)
            && AllHealthy(record.PreDrainRead, record.RequiredNodes)
            && record.DrainPollSawDraining
            && record.CancelPollLeftDraining;
        if (!rest)
        {
            failures.Add($"REST steps: [{string.Join(", ", record.Steps.Select(s => $"{s.Name} {s.Status} (expected {s.Expected})"))}]; " +
                         $"fixture read healthy {AllHealthy(record.FixtureRead, record.RequiredNodes)}, pre-drain read healthy " +
                         $"{AllHealthy(record.PreDrainRead, record.RequiredNodes)}, '{record.DrainedNode}' seen draining {record.DrainPollSawDraining}, " +
                         $"left draining after the cancel {record.CancelPollLeftDraining}.");
        }

        // (5) The host client is still connected at the end of the run.
        var hostConnected = string.Equals(record.HostClientStateAtEnd, "Connected", StringComparison.Ordinal);
        if (!hostConnected)
        {
            failures.Add($"The host client reads '{record.HostClientStateAtEnd}' at the end of the run — expected Connected.");
        }

        return new ClusterFramesVerdict(liveness && noFrames && noRows && rest && hostConnected,
            liveness, noFrames, noRows, rest, hostConnected, failures);
    }

    private static async Task<RestStep> SendAsync(
        HttpClient api, string name, HttpMethod method, string path, string? jsonBody, string expected, Func<int, bool> ok, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path.TrimStart('/'));
        if (jsonBody is not null)
        {
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var resp = await api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        Log($"{method} {path} -> {status}");
        return new RestStep(name, method.Method, path, status, expected, ok(status), Truncate(text, 2000), DateTimeOffset.UtcNow);
    }

    private static async Task<NodeRead> ReadNodesAsync(HttpClient api, string label, CancellationToken ct)
    {
        using var resp = await api.GetAsync(NodesPath.TrimStart('/'), ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in doc.RootElement.EnumerateArray())
                {
                    if (n.TryGetProperty("nodeId", out var id) && n.TryGetProperty("state", out var st))
                    {
                        states[id.GetString() ?? string.Empty] = st.GetString() ?? string.Empty;
                    }
                }
            }
        }

        return new NodeRead(label, DateTimeOffset.UtcNow, (int)resp.StatusCode, states, Truncate(text, 4000));
    }

    private static async Task<NodeRead> PollNodesAsync(
        HttpClient api, string label, TimeSpan max, List<NodeRead> reads, Func<NodeRead, bool> done, CancellationToken ct)
    {
        var end = DateTimeOffset.UtcNow + max;
        while (true)
        {
            var read = await ReadNodesAsync(api, label, ct).ConfigureAwait(false);
            if (done(read) || DateTimeOffset.UtcNow >= end)
            {
                reads.Add(read);
                Log($"{label}: {Describe(read)}");
                return read;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); // fence-allow: LOOP-DRIVER — the declared bounded poll of the cluster REST surface
        }
    }

    private static async Task<IReadOnlyList<string>> BridgeMetricsAsync(string apiBaseUrl, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var text = await http.GetStringAsync($"{apiBaseUrl}/metrics", ct).ConfigureAwait(false);
            return text.Split('\n').Where(l => l.StartsWith("bridge_", StringComparison.Ordinal)).Select(l => l.TrimEnd('\r')).ToList();
        }
        catch (HttpRequestException ex)
        {
            return [$"# /metrics failed: {ex.Message}"];
        }
    }

    private static string Describe(NodeRead r) =>
        $"HTTP {r.Status} [{string.Join(", ", r.States.Select(kv => $"{kv.Key}={kv.Value}"))}]";

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}");
}
