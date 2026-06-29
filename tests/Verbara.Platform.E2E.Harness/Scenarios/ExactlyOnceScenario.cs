using Verbara.Platform.E2E.Harness.Audit;
using Verbara.Platform.E2E.Harness.Auth;
using Verbara.Platform.E2E.Harness.Config;
using Verbara.Platform.E2E.Harness.Hub;
using Verbara.Platform.E2E.Harness.Reports;
using Verbara.Platform.E2E.Harness.Trigger;
using Verbara.Platform.Realtime.Contracts.Dtos;

namespace Verbara.Platform.E2E.Harness.Scenarios;

/// <summary>
/// Walking-skeleton scenario that closes the Plan B Test 5 PARTIAL
/// finding (smoke report 2026-05-24). Asserts the leader-gate invariant
/// end-to-end against the live Talos lab:
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariants (PASS criteria):</b>
/// </para>
/// <list type="number">
/// <item><description>Every connected SignalR client receives <i>exactly</i>
/// <c>EventCount</c> <c>OnAgentStateChanged</c> messages (no duplicates,
/// no drops).</description></item>
/// <item><description>Aggregated across all Realtime pods, the audit
/// endpoint reports <i>exactly</i> <c>EventCount</c> <see cref="RelayOutcomeKind.Forwarded"/>
/// outcomes — only the leader pod contributes any.</description></item>
/// <item><description>Aggregated across all Realtime pods, the audit
/// endpoint reports <i>exactly</i> <c>EventCount × (PodCount - 1)</c>
/// <see cref="RelayOutcomeKind.SkippedNotLeader"/> outcomes — every follower
/// short-circuited per ADR-0022 Phase A.5.</description></item>
/// <item><description>Exactly one pod is identified as Leader during the
/// trigger window — multi-leader would indicate broken lock semantics.</description></item>
/// </list>
/// </remarks>
internal static class ExactlyOnceScenario
{
    public const string Name = "exactly-once";

    public static async Task<ScenarioReport> RunAsync(HarnessConfig config, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var failures = new List<string>();
        var warnings = new List<string>();

        Log($"Starting scenario '{Name}' against {config.AuditBaseUrls.Count} Realtime pod(s).");

        // 1. Login twice — PlatformAdmin for audit endpoint, Agent for hub + trigger
        var auth = new PlatformAuthClient(config.ApiBaseUrl);
        Log($"Logging in as PlatformAdmin '{config.PlatformAdminEmail}' (tenant '{config.AdminTenant}')...");
        var adminToken = await auth.LoginAsync(config.PlatformAdminEmail, config.PlatformAdminPassword, config.AdminTenant, ct).ConfigureAwait(false);
        Log($"Logging in as Agent '{config.Email}' (tenant '{config.Tenant}')...");
        var agentToken = await auth.LoginAsync(config.Email, config.Password, config.Tenant, ct).ConfigureAwait(false);

        // 2. Connect SignalR clients
        Log($"Connecting {config.ClientCount} SignalR client(s) to {config.RealtimeHubUrl}...");
        await using var hubPool = new SignalRClientPool(config.RealtimeHubUrl, agentToken);
        await hubPool.ConnectAsync(config.ClientCount, ct).ConfigureAwait(false);
        Log($"All {hubPool.Count} clients connected. Waiting 2s for tenant-group join to settle.");
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); // fence-allow: SETTLE — let the SignalR tenant-group join settle against the live hub

        // 3. Baseline cursor — small back-shift so we don't lose an edge race
        var baselineTs = DateTimeOffset.UtcNow.AddMilliseconds(-500);
        Log($"Baseline cursor for audit queries: {baselineTs:O}");

        // 4. Trigger N agent state changes
        using var trigger = new AgentStateTrigger(config.ApiBaseUrl, agentToken, config.Tenant);
        Log($"Triggering {config.EventCount} AgentStateChanged event(s) via PUT /agents/me/state...");
        var triggerTimestamps = await trigger.EmitAsync(config.EventCount, ct).ConfigureAwait(false);
        Log($"All {triggerTimestamps.Count} events emitted. Settling for {config.SettleDelay.TotalSeconds:F1}s before reading audit.");

        // 5. Wait for fanout to settle
        await Task.Delay(config.SettleDelay, ct).ConfigureAwait(false); // fence-allow: SETTLE — settle before reading the audit on the live resource

        // 6. Per-pod audit fetch (parallel)
        Log($"Fetching audit pages from {config.AuditBaseUrls.Count} pod(s)...");
        var auditClients = config.AuditBaseUrls
            .Select(url => new AuditClient(url, adminToken))
            .ToList();
        try
        {
            var auditTasks = auditClients
                .Select(c => c.FetchAsync(baselineTs, limit: 10_000, ct))
                .ToList();
            var pages = await Task.WhenAll(auditTasks).ConfigureAwait(false);
            var aggregate = MultiPodAuditAggregator.Compute(pages);
            warnings.AddRange(aggregate.EvictionWarnings);

            // 7. Assertions
            var expectedForwarded = config.EventCount;
            var expectedSkippedNotLeader = config.EventCount * (pages.Length - 1);

            if (aggregate.ForwardedCount != expectedForwarded)
            {
                failures.Add(
                    $"Total Forwarded mismatch: expected {expectedForwarded}, got {aggregate.ForwardedCount}. " +
                    $"Per-pod: {string.Join(", ", aggregate.PerPodForwardedCount.Select(kv => $"{kv.Key}={kv.Value}"))}.");
            }

            if (aggregate.SkippedNotLeaderCount != expectedSkippedNotLeader)
            {
                failures.Add(
                    $"Total SkippedNotLeader mismatch: expected {expectedSkippedNotLeader}, got {aggregate.SkippedNotLeaderCount}. " +
                    $"Per-pod: {string.Join(", ", aggregate.PerPodSkippedNotLeaderCount.Select(kv => $"{kv.Key}={kv.Value}"))}.");
            }

            if (aggregate.LeaderPodInstanceIds.Count != 1)
            {
                failures.Add(
                    $"Expected exactly 1 leader pod (multi-leader = broken lock semantics). " +
                    $"Got {aggregate.LeaderPodInstanceIds.Count}: [{string.Join(", ", aggregate.LeaderPodInstanceIds)}].");
            }

            if (aggregate.ForwardErrorCount > 0)
            {
                failures.Add($"{aggregate.ForwardErrorCount} ForwardError outcome(s) observed — Redis backplane / group serialisation issue.");
            }

            if (aggregate.SkippedNullTenantCount > 0)
            {
                warnings.Add($"{aggregate.SkippedNullTenantCount} SkippedNullTenant outcome(s) observed — agent JWT may be missing tid claim.");
            }

            // 8. Per-client receive parity
            var expectedReceives = config.EventCount;
            for (var i = 0; i < hubPool.ReceivedCounts.Count; i++)
            {
                var got = hubPool.ReceivedCounts[i];
                if (got != expectedReceives)
                {
                    failures.Add(
                        $"Client #{i} received {got} OnAgentStateChanged message(s), expected {expectedReceives}. " +
                        $"Indicates {(got < expectedReceives ? "lost message" : "DUPLICATE — exactly-once contract broken")}.");
                }
            }

            var completedAt = DateTimeOffset.UtcNow;
            var report = new ScenarioReport(
                ScenarioName: Name,
                Topology: "talos",
                StartedAt: startedAt,
                CompletedAt: completedAt,
                Passed: failures.Count == 0,
                Failures: failures,
                Warnings: warnings,
                PodCount: aggregate.PodCount,
                ClientCount: hubPool.Count,
                EventsEmitted: triggerTimestamps.Count,
                ExpectedReceivesPerClient: expectedReceives,
                ActualReceivesPerClient: hubPool.ReceivedCounts.ToList(),
                TotalForwarded: aggregate.ForwardedCount,
                TotalSkippedNotLeader: aggregate.SkippedNotLeaderCount,
                LeaderPodInstanceIds: aggregate.LeaderPodInstanceIds,
                PerPodForwarded: aggregate.PerPodForwardedCount,
                PerPodSkippedNotLeader: aggregate.PerPodSkippedNotLeaderCount,
                AuditBaseUrls: config.AuditBaseUrls);

            return report;
        }
        finally
        {
            foreach (var c in auditClients)
            {
                c.Dispose();
            }
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}");
}
