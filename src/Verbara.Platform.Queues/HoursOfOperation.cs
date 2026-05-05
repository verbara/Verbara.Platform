namespace Verbara.Platform.Queues;

public sealed class HoursOfOperation
{
    public string TimezoneId { get; }

    private readonly Dictionary<DayOfWeek, (TimeOnly Open, TimeOnly Close)> _schedule = new();

    public HoursOfOperation(string timezoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timezoneId);
        TimezoneId = timezoneId;
    }

    public void SetDaySchedule(DayOfWeek day, TimeOnly open, TimeOnly close)
    {
        if (close <= open)
            throw new ArgumentException("Close time must be after open time.");
        _schedule[day] = (open, close);
    }

    public bool IsOpen(DateTimeOffset utcNow)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(TimezoneId);
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);
        var dayOfWeek = local.DayOfWeek;
        var timeOfDay = TimeOnly.FromTimeSpan(local.TimeOfDay);

        if (!_schedule.TryGetValue(dayOfWeek, out var schedule))
            return false;

        return timeOfDay >= schedule.Open && timeOfDay < schedule.Close;
    }

    public static HoursOfOperation AlwaysOpen()
    {
        var hours = new HoursOfOperation("UTC");
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
            hours._schedule[day] = (TimeOnly.MinValue, TimeOnly.MaxValue);
        return hours;
    }
}
