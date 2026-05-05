using System.Collections.Concurrent;

namespace Verbara.Platform.Api.Endpoints.Retention;

/// <summary>
/// In-process record of the most recent execution per <c>IRetentionTarget.Name</c>.
/// Survives only within one process lifetime — survives container restarts
/// would require a persistent store (deferred per R5.2 scope).
/// </summary>
/// <remarks>
/// Updated by <see cref="RetentionAdminService"/> on each manual run. The
/// scheduled <c>RetentionService</c> background loop does <em>not</em> feed
/// this tracker today (Pro doesn't expose a hook); the admin UI surfaces
/// "Never run (this process)" until the operator triggers a manual run or
/// until a future Pro release adds an <c>IRetentionTargetExecutionListener</c>
/// abstraction. Documented as a known limitation in the page banner.
/// </remarks>
public sealed class RetentionExecutionTracker
{
    private readonly ConcurrentDictionary<string, RetentionExecutionRecord> _records = new(StringComparer.Ordinal);

    public void Record(
        string targetName,
        DateTimeOffset at,
        long rowsPurged,
        string status,
        bool wasDryRun)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetName);
        ArgumentException.ThrowIfNullOrEmpty(status);
        _records[targetName] = new RetentionExecutionRecord(at, rowsPurged, status, wasDryRun);
    }

    public RetentionExecutionRecord? Get(string targetName)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetName);
        return _records.TryGetValue(targetName, out var record) ? record : null;
    }
}

public sealed record RetentionExecutionRecord(
    DateTimeOffset At,
    long RowsPurged,
    string Status,
    bool WasDryRun);
