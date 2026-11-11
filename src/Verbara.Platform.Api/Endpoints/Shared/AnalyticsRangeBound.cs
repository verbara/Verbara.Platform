using System.Globalization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Reads the <c>from</c> and <c>to</c> query bounds of the analytics endpoints
/// (<c>/analytics/*</c> and <c>/call-analytics/*</c>).
/// </summary>
/// <remarks>
/// <para>
/// A bound is either an instant (<c>2026-03-04T12:00:00Z</c>) or a date (<c>2026-03-04</c>). The
/// console's analytics filter sends dates: its default range and its presets end on today's date.
/// </para>
/// <para>
/// A date is a UTC day, because the console builds it from the UTC date
/// (<c>toISOString().slice(0, 10)</c>) and no tenant or user time zone reaches these endpoints. A
/// date-only <c>from</c> starts at that day's 00:00 UTC. A date-only <c>to</c> includes that whole
/// day: it ends where the next day starts, exclusive. The stores compare the upper bound
/// inclusively (<c>&lt;= @To</c>), so the exclusive end is passed as the day's last microsecond,
/// the finest step a PostgreSQL <c>timestamptz</c> holds.
/// </para>
/// <para>
/// An instant keeps its meaning and is only normalised to offset zero (<see cref="UtcInstantExtensions"/>).
/// </para>
/// </remarks>
internal static class AnalyticsRangeBound
{
    private const string DateOnlyFormat = "yyyy-MM-dd";

    /// <summary>Reads a <c>from</c> bound. A malformed value throws <see cref="FormatException"/>.</summary>
    public static DateTimeOffset? ParseFrom(string? value)
    {
        if (value is null) return null;
        return TryParseDay(value, out var day)
            ? StartOfUtcDay(day)
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUtcInstant();
    }

    /// <summary>Reads a <c>to</c> bound. A malformed value throws <see cref="FormatException"/>.</summary>
    public static DateTimeOffset? ParseTo(string? value)
    {
        if (value is null) return null;
        return TryParseDay(value, out var day)
            ? EndOfUtcDay(day)
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUtcInstant();
    }

    /// <summary>Reads a <c>from</c> bound; false when it is absent or malformed.</summary>
    public static bool TryParseFrom(string? value, out DateTimeOffset bound)
    {
        if (value is not null && TryParseDay(value, out var day))
        {
            bound = StartOfUtcDay(day);
            return true;
        }
        return TryParseInstant(value, out bound);
    }

    /// <summary>Reads a <c>to</c> bound; false when it is absent or malformed.</summary>
    public static bool TryParseTo(string? value, out DateTimeOffset bound)
    {
        if (value is not null && TryParseDay(value, out var day))
        {
            bound = EndOfUtcDay(day);
            return true;
        }
        return TryParseInstant(value, out bound);
    }

    private static bool TryParseDay(string value, out DateOnly day)
        => DateOnly.TryParseExact(value, DateOnlyFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static bool TryParseInstant(string? value, out DateTimeOffset bound)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            bound = parsed.ToUtcInstant();
            return true;
        }
        bound = default;
        return false;
    }

    private static DateTimeOffset StartOfUtcDay(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static DateTimeOffset EndOfUtcDay(DateOnly day)
        => day == DateOnly.MaxValue
            ? DateTimeOffset.MaxValue
            : StartOfUtcDay(day.AddDays(1)).AddTicks(-TimeSpan.TicksPerMicrosecond);
}
