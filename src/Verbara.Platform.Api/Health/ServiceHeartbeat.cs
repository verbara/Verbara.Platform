using System.Collections.Concurrent;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// Thread-safe implementation of <see cref="IServiceHeartbeat"/>.
/// A service is healthy if it ticked within 3x its expected interval.
/// </summary>
internal sealed class ServiceHeartbeat : IServiceHeartbeat
{
    private const double HealthMultiplier = 3.0;

    private readonly ConcurrentDictionary<string, (DateTimeOffset LastTick, TimeSpan ExpectedInterval)> _ticks = new();

    public void RecordTick(string serviceName, TimeSpan expectedInterval)
    {
        _ticks[serviceName] = (DateTimeOffset.UtcNow, expectedInterval);
    }

    public bool IsHealthy(string serviceName)
    {
        if (!_ticks.TryGetValue(serviceName, out var info))
            return true; // Not registered yet = healthy (hasn't started)

        var elapsed = DateTimeOffset.UtcNow - info.LastTick;
        return elapsed <= info.ExpectedInterval * HealthMultiplier;
    }

    public IReadOnlyDictionary<string, ServiceHeartbeatInfo> GetAll()
    {
        var result = new Dictionary<string, ServiceHeartbeatInfo>();
        foreach (var (name, (lastTick, interval)) in _ticks)
        {
            var elapsed = DateTimeOffset.UtcNow - lastTick;
            result[name] = new ServiceHeartbeatInfo(lastTick, interval, elapsed <= interval * HealthMultiplier);
        }
        return result;
    }
}
