namespace Asterisk.Platform.Automation;

public interface ITimerStore
{
    Task SaveAsync(ScheduledTimer timer, CancellationToken ct);
    Task<IReadOnlyList<ScheduledTimer>> GetOverdueAsync(DateTimeOffset now, int limit, CancellationToken ct);
    Task MarkFiredAsync(ScheduledTimer timer, CancellationToken ct);
}
