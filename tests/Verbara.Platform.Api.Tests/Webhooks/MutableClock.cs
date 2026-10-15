using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Webhooks;

/// <summary>
/// An <see cref="IClock"/> that a test moves by hand. Read from the delivery service's threads and written by the
/// test, so the instant is kept as UTC ticks behind <see cref="Interlocked"/> (a <see cref="DateTimeOffset"/>
/// field can tear).
/// </summary>
internal sealed class MutableClock(DateTimeOffset start) : IClock
{
    private long _utcTicks = start.UtcTicks;

    public DateTimeOffset UtcNow
    {
        get => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
    }

    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
}
