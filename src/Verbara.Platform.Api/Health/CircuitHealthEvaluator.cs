using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// Reads the observed state of a set of circuit keys for a health check (Frente D, v1.9.1). The <c>services</c> and
/// <c>postgres</c> checks call it. It lived on <see cref="AsteriskAmiHealthCheck"/> until that check stopped reading
/// circuits (readiness-without-asterisk, design D2).
/// </summary>
internal static class CircuitHealthEvaluator
{
    /// <summary>
    /// Healthy when every watched key is closed or unobserved, Degraded when one is open for less than
    /// <paramref name="unhealthyThreshold"/>, Unhealthy when one has been open for longer. The data carries each
    /// key's state, <c>stateSince</c> and duration, or <c>unobserved</c>.
    /// </summary>
    internal static (HealthStatus Status, string Message, IReadOnlyDictionary<string, object> Data)
        EvaluateCircuits(
            IList<string> watchedKeys,
            TimeSpan unhealthyThreshold,
            IResilienceStateObserver observer)
    {
        var openRecent = new List<string>();
        var openLong = new List<string>();
        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var key in watchedKeys)
        {
            var snap = observer.GetSnapshot(key);
            if (snap is null)
            {
                data[key] = "unobserved";
                continue;
            }

            data[key] = new
            {
                state = snap.State.ToString(),
                stateSince = snap.StateSince,
                durationSeconds = Math.Round(snap.Duration.TotalSeconds, 1),
            };

            if (!snap.IsOpen)
                continue;

            if (snap.Duration >= unhealthyThreshold)
                openLong.Add(key);
            else
                openRecent.Add(key);
        }

        if (openLong.Count > 0)
        {
            return (
                HealthStatus.Unhealthy,
                $"Circuit open >{unhealthyThreshold.TotalMinutes:0}min: {string.Join(", ", openLong)}",
                data);
        }

        if (openRecent.Count > 0)
        {
            return (
                HealthStatus.Degraded,
                $"Circuit open: {string.Join(", ", openRecent)}",
                data);
        }

        return (HealthStatus.Healthy, "All watched circuits closed", data);
    }
}
