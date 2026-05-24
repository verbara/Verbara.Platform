using System.Collections.Generic;

namespace Verbara.Platform.Realtime.Contracts.Dtos;

/// <summary>
/// Response payload for <c>GET /admin/realtime/audit?since=&amp;limit=</c>.
/// Wraps the ring-buffer slice in enough context for an E2E harness to detect
/// silent eviction (so a "0 forwards observed" assertion never lies because
/// the ring rolled over between the test trigger and the audit fetch).
/// </summary>
/// <param name="PodInstanceId">Identity of the pod that served this audit response.</param>
/// <param name="Capacity">Maximum entries the ring buffer holds before overwriting the oldest.</param>
/// <param name="CurrentSize">Entries currently in the ring (≤ <see cref="Capacity"/>).</param>
/// <param name="EvictedSinceStart">Total entries silently overwritten since pod start — non-zero means harness queries should narrow the window or raise <c>limit</c>.</param>
/// <param name="OldestRetainedTs">UTC timestamp of the oldest entry still in the buffer (null when buffer is empty). If <c>since</c> precedes this value, the harness MUST treat the result as incomplete.</param>
/// <param name="Entries">Outcome rows matching the <c>since</c>/<c>limit</c> filter, ordered oldest-to-newest.</param>
public sealed record RelayOutcomePage(
    string PodInstanceId,
    int Capacity,
    int CurrentSize,
    long EvictedSinceStart,
    DateTimeOffset? OldestRetainedTs,
    IReadOnlyList<RelayOutcomeEntry> Entries);
