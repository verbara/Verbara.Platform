using Verbara.Platform.Realtime.Contracts.Dtos;

namespace Verbara.Platform.E2E.Harness.Audit;

/// <summary>
/// Aggregates <see cref="RelayOutcomePage"/> snapshots from every Realtime
/// pod and runs the per-event-window leader-gate invariant: across all
/// pods, every triggered event must produce <b>exactly one</b>
/// <see cref="RelayOutcomeKind.Forwarded"/> outcome and <b>N-1</b>
/// <see cref="RelayOutcomeKind.SkippedNotLeader"/> outcomes
/// (where N = number of pods).
/// </summary>
internal sealed record MultiPodAuditAggregate(
    int PodCount,
    int ForwardedCount,
    int SkippedNotLeaderCount,
    int SkippedNullTenantCount,
    int SkippedNullNodeIdCount,
    int ForwardErrorCount,
    IReadOnlyList<string> LeaderPodInstanceIds,
    IReadOnlyDictionary<string, int> PerPodForwardedCount,
    IReadOnlyDictionary<string, int> PerPodSkippedNotLeaderCount,
    IReadOnlyList<string> EvictionWarnings);

internal static class MultiPodAuditAggregator
{
    public static MultiPodAuditAggregate Compute(IReadOnlyList<RelayOutcomePage> pages)
    {
        var forwarded = 0;
        var skipNotLeader = 0;
        var skipNullTenant = 0;
        var skipNullNodeId = 0;
        var forwardError = 0;
        var leaders = new HashSet<string>(StringComparer.Ordinal);
        var perPodForwarded = new Dictionary<string, int>(StringComparer.Ordinal);
        var perPodSkipNotLeader = new Dictionary<string, int>(StringComparer.Ordinal);
        var evictionWarnings = new List<string>();

        foreach (var page in pages)
        {
            if (page.EvictedSinceStart > 0)
            {
                evictionWarnings.Add(
                    $"Pod {page.PodInstanceId}: {page.EvictedSinceStart} entries evicted before this snapshot — narrow the 'since' window or raise RealtimeAudit:Capacity.");
            }

            var podForwarded = 0;
            var podSkipNotLeader = 0;

            foreach (var entry in page.Entries)
            {
                switch (entry.Outcome)
                {
                    case RelayOutcomeKind.Forwarded:
                        forwarded++;
                        podForwarded++;
                        leaders.Add(page.PodInstanceId);
                        break;
                    case RelayOutcomeKind.SkippedNotLeader:
                        skipNotLeader++;
                        podSkipNotLeader++;
                        break;
                    case RelayOutcomeKind.SkippedNullTenant:
                        skipNullTenant++;
                        break;
                    case RelayOutcomeKind.SkippedNullNodeId:
                        skipNullNodeId++;
                        break;
                    case RelayOutcomeKind.ForwardError:
                        forwardError++;
                        break;
                }
            }

            perPodForwarded[page.PodInstanceId] = podForwarded;
            perPodSkipNotLeader[page.PodInstanceId] = podSkipNotLeader;
        }

        return new MultiPodAuditAggregate(
            PodCount: pages.Count,
            ForwardedCount: forwarded,
            SkippedNotLeaderCount: skipNotLeader,
            SkippedNullTenantCount: skipNullTenant,
            SkippedNullNodeIdCount: skipNullNodeId,
            ForwardErrorCount: forwardError,
            LeaderPodInstanceIds: leaders.ToList(),
            PerPodForwardedCount: perPodForwarded,
            PerPodSkippedNotLeaderCount: perPodSkipNotLeader,
            EvictionWarnings: evictionWarnings);
    }
}
