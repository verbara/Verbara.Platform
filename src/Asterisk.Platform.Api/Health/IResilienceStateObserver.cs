using Asterisk.Sdk.Resilience.Diagnostics;

namespace Asterisk.Platform.Api.Health;

/// <summary>
/// Read-only view of the <see cref="ResilienceMetrics.CircuitStateGauge"/> for
/// HealthCheck consumers. Populated by <see cref="ResilienceStateObserver"/>
/// which listens to the <c>Asterisk.Sdk.Resilience</c> meter.
/// </summary>
public interface IResilienceStateObserver
{
    /// <summary>
    /// Snapshot of every policy key the observer has seen at least once.
    /// </summary>
    IReadOnlyDictionary<string, ResilienceCircuitSnapshot> GetAll();

    /// <summary>
    /// Current state for a specific policy key, or <see langword="null"/> if never observed.
    /// </summary>
    ResilienceCircuitSnapshot? GetSnapshot(string policyKey);
}

/// <summary>Snapshot of the circuit state for a given policy key.</summary>
/// <param name="PolicyKey">The keyed policy name (e.g., <c>webhook.delivery</c>).</param>
/// <param name="State">Closed / HalfOpen / Open.</param>
/// <param name="StateSince">When the current <paramref name="State"/> was first observed.</param>
public sealed record ResilienceCircuitSnapshot(
    string PolicyKey,
    ResilienceCircuitState State,
    DateTimeOffset StateSince)
{
    /// <summary>How long the circuit has been in <see cref="State"/>.</summary>
    public TimeSpan Duration => DateTimeOffset.UtcNow - StateSince;

    /// <summary>True if the circuit is Open (traffic rejected).</summary>
    public bool IsOpen => State == ResilienceCircuitState.Open;
}

/// <summary>
/// Mirrors <see cref="ResilienceMetrics.CircuitStateValue"/> so Platform.Health
/// callers don't need to reference the SDK diagnostics enum directly.
/// </summary>
public enum ResilienceCircuitState
{
    /// <summary>Traffic flows normally.</summary>
    Closed = 0,

    /// <summary>Single probe in flight after an open duration elapsed.</summary>
    HalfOpen = 1,

    /// <summary>Traffic is being rejected fast.</summary>
    Open = 2,
}
