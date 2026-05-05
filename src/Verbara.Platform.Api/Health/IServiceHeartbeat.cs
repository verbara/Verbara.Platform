namespace Verbara.Platform.Api.Health;

/// <summary>
/// Tracks heartbeat ticks from background services for health monitoring.
/// </summary>
public interface IServiceHeartbeat
{
    void RecordTick(string serviceName, TimeSpan expectedInterval);
    bool IsHealthy(string serviceName);
    IReadOnlyDictionary<string, ServiceHeartbeatInfo> GetAll();
}

public sealed record ServiceHeartbeatInfo(
    DateTimeOffset LastTick,
    TimeSpan ExpectedInterval,
    bool IsHealthy);
