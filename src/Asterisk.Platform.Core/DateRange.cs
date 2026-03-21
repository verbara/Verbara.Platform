namespace Asterisk.Platform.Core;

public sealed record DateRange
{
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }

    public DateRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end < start)
            throw new ArgumentException("End must be >= Start.", nameof(end));
        Start = start;
        End = end;
    }

    public bool Contains(DateTimeOffset point) => point >= Start && point <= End;

    public TimeSpan Duration => End - Start;
}
