using Verbara.Platform.E2E.Harness.Hub;

namespace Verbara.Platform.E2E.Harness.Scenarios;

/// <summary>
/// Offline negative controls for <see cref="ClusterFramesScenario.Evaluate"/> (platform-drops-the-cluster-bridge,
/// task 1.2): synthetic records with a known verdict, run with <c>HARNESS_SCENARIO=cluster-frames-selfcheck</c> and no
/// lab. Each case must get the verdict it declares; a case that does not shows the evaluator cannot tell that record
/// apart from a pass.
/// </summary>
internal static class ClusterFramesSelfCheck
{
    public const string Name = "cluster-frames-selfcheck";

    private const int K = 3;

    /// <summary>Runs every case; returns the failures (empty when every verdict is the declared one).</summary>
    public static IReadOnlyList<string> Run()
    {
        var declared = Declared();
        var oneClusterFrame = new ClusterFrame(
            """{"nodeId":"primary","previousState":"Healthy","newState":"Draining","changedAt":"2026-11-21T00:00:00Z"}""",
            DateTimeOffset.UnixEpoch);
        var onlyPrimaryHealthy = Nodes(("primary", "healthy"));

        (string Case, ClusterFramesRecord Record, bool ExpectedPass)[] cases =
        [
            ("the declared record", declared, true),
            ("one cluster frame", declared with { ClusterFrames = [oneClusterFrame] }, false),
            ("one cluster frame on the liveness client", declared with { LivenessClientClusterFrames = [oneClusterFrame] }, false),
            ("host client Disconnected at the end", declared with { HostClientStateAtEnd = "Disconnected" }, false),
            ("host client Reconnecting at the end", declared with { HostClientStateAtEnd = "Reconnecting" }, false),
            ("K - 1 agent frames", declared with { AgentFramesInLiveness = K - 1 }, false),
            ("K - 1 Forwarded agent.state_changed rows", declared with { AgentForwardedRowsInLiveness = K - 1 }, false),
            ("one cluster audit row", declared with { ClusterAuditRows = 1 }, false),
            ("pre-drain read with only primary healthy", declared with { PreDrainRead = declared.PreDrainRead! with { States = onlyPrimaryHealthy } }, false),
            ("fixture read with only primary healthy", declared with { FixtureRead = declared.FixtureRead! with { States = onlyPrimaryHealthy } }, false),
            ("drain poll never saw draining", declared with { DrainPollSawDraining = false }, false),
            ("drain answered 400", declared with { Steps = [.. declared.Steps.Select(s => s.Name == "drain" ? s with { Status = 400, Ok = false } : s)] }, false),
        ];

        var failures = new List<string>();
        foreach (var (name, record, expectedPass) in cases)
        {
            var verdict = ClusterFramesScenario.Evaluate(record);
            if (verdict.Passed != expectedPass)
            {
                failures.Add($"Case '{name}': expected {(expectedPass ? "PASS" : "FAIL")}, got {(verdict.Passed ? "PASS" : "FAIL")}.");
            }
        }

        return failures;
    }

    private static ClusterFramesRecord Declared()
    {
        var bothHealthy = Nodes(("primary", "healthy"), ("lab-node-2", "healthy"));
        RestStep Step(string name, string method, string path, int status, string expected) =>
            new(name, method, path, status, expected, Ok: true, ResponseBody: null, At: DateTimeOffset.UnixEpoch);
        return new ClusterFramesRecord(
            ExpectedAgentFrames: K,
            AgentFramesInLiveness: K,
            AgentForwardedRowsInLiveness: K,
            ClusterFrames: [],
            LivenessClientClusterFrames: [],
            ClusterAuditRows: 0,
            Steps:
            [
                Step("register-node", "POST", "/api/v1/management/cluster/nodes", 201, "201"),
                Step("drain", "POST", "/api/v1/management/cluster/nodes/primary/drain", 202, "202"),
                Step("cancel-drain", "DELETE", "/api/v1/management/cluster/nodes/primary/drain", 200, "2xx"),
            ],
            RequiredNodes: ["primary", "lab-node-2"],
            DrainedNode: "primary",
            FixtureRead: new NodeRead("fixture", DateTimeOffset.UnixEpoch, 200, bothHealthy, "[]"),
            PreDrainRead: new NodeRead("pre-drain", DateTimeOffset.UnixEpoch, 200, bothHealthy, "[]"),
            DrainPollSawDraining: true,
            CancelPollLeftDraining: true,
            HostClientStateAtEnd: "Connected");
    }

    private static Dictionary<string, string> Nodes(params (string Id, string State)[] nodes) =>
        nodes.ToDictionary(n => n.Id, n => n.State, StringComparer.Ordinal);
}
