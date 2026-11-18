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
    /// Healthy when every watched key is closed or unobserved, Degraded when one is not closed (open or half-open),
    /// Unhealthy only when one has read <c>Open</c> for <paramref name="unhealthyThreshold"/> or longer. A half-open
    /// key never escalates: since Sdk 2.10 the gauge reads the breaker's current state, so a tripped circuit reads
    /// <c>HalfOpen</c> once its open window ends, and a circuit that keeps cycling never gives an unbroken <c>Open</c>
    /// reading. With no threshold (<see langword="null"/>), a key that is not closed is Degraded at most, whatever its
    /// duration, and the message names every such key with its state: the <c>services</c> check passes that, because a
    /// dependency circuit never gates readiness (readiness-without-asterisk, design D3). The data carries each key's
    /// state, <c>stateSince</c> and duration, or <c>unobserved</c>.
    /// </summary>
    internal static (HealthStatus Status, string Message, IReadOnlyDictionary<string, object> Data)
        EvaluateCircuits(
            IList<string> watchedKeys,
            TimeSpan? unhealthyThreshold,
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

            if (snap.State == ResilienceCircuitState.Closed)
                continue;

            if (snap.IsOpen && unhealthyThreshold is { } threshold && snap.Duration >= threshold)
                openLong.Add(key);
            else
                openRecent.Add($"{key} ({snap.State})");
        }

        // openLong is filled only when a threshold was given.
        if (openLong.Count > 0)
        {
            return (
                HealthStatus.Unhealthy,
                $"Circuit open >{unhealthyThreshold.GetValueOrDefault().TotalMinutes:0}min: {string.Join(", ", openLong)}",
                data);
        }

        if (openRecent.Count > 0)
        {
            return (
                HealthStatus.Degraded,
                $"Circuit not closed: {string.Join(", ", openRecent)}",
                data);
        }

        return (HealthStatus.Healthy, "All watched circuits closed", data);
    }
}
