namespace Verbara.Platform.Queues.Licensing;

/// <summary>
/// The day cut of the daily close (licensed-agent-daily-close, design D7): a day runs from local midnight
/// to the next local midnight in the deployment's day zone, resolved through <see cref="TimeZoneInfo"/>, so
/// a daylight-saving day lasts 23 or 25 hours, never a fixed 24.
/// </summary>
public static class LicenseAgentDayZone
{
    /// <summary>The configuration key of the day zone (an IANA zone id; UTC when unset).</summary>
    public const string ConfigurationKey = "Licensing:Metering:DayZone";

    public const string DefaultZone = "UTC";

    /// <summary>Resolves an IANA zone id; an unknown id is refused with an error that names it.</summary>
    public static TimeZoneInfo Resolve(string zoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException(
                $"The licensed-agent day zone '{zoneId}' ({ConfigurationKey}) is not a time zone this host knows.",
                nameof(zoneId), ex);
        }
    }

    /// <summary>The UTC instants at which <paramref name="day"/> starts (inclusive) and ends (exclusive).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Bounds(DateOnly day, TimeZoneInfo zone) =>
        (StartOf(day, zone), StartOf(day.AddDays(1), zone));

    /// <summary>The day, in <paramref name="zone"/>, that contains <paramref name="instant"/>.</summary>
    public static DateOnly DayOf(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }

    // The first instant of the local day. Local midnight may not exist (a zone whose clocks spring forward at
    // midnight): the day then starts at the first local time that does. It may exist twice (clocks falling
    // back at midnight): the day starts at its first occurrence, the one with the larger UTC offset.
    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // Skip forward minute by minute to the first valid local time (a gap is at most a few hours).
            var probe = local;
            while (zone.IsInvalidTime(probe))
                probe = probe.AddMinutes(1);
            return new DateTimeOffset(probe, zone.GetUtcOffset(probe)).ToUniversalTime();
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
