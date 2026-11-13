namespace Verbara.Platform.Api.Endpoints.Shared;

// Query-string date-range bounds that keep a date-only value apart from a midnight instant.
//
// A parameter bound as DateTimeOffset turns "2026-03-04" into 00:00 of that day, so a date-only upper
// bound excluded the day it names. These types bind through their static TryParse (the minimal-API
// binding pattern, generated at compile time, no reflection) and read the raw value with
// DateRangeBound. A malformed value still fails binding, so the endpoint answers 400 exactly as the
// DateTimeOffset parameter did.
//
// Declare these parameters without [FromQuery]: they bind from the query string either way (a type
// with TryParse is never read from the body), and the OpenAPI document then shows them as strings.
// With the attribute it would document the record's own shape instead.

/// <summary>A range's lower bound: a date-only value starts at that day's 00:00 UTC.</summary>
internal readonly record struct DateRangeStart(DateTimeOffset Value)
{
    public static bool TryParse(string? value, IFormatProvider? provider, out DateRangeStart result)
    {
        var ok = DateRangeBound.TryParseFrom(value, out var bound);
        result = new DateRangeStart(bound);
        return ok;
    }
}

/// <summary>
/// An inclusive upper bound (the store compares <c>&lt;=</c>): a date-only value includes that whole UTC day.
/// </summary>
internal readonly record struct DateRangeEnd(DateTimeOffset Value)
{
    public static bool TryParse(string? value, IFormatProvider? provider, out DateRangeEnd result)
    {
        var ok = DateRangeBound.TryParseTo(value, out var bound);
        result = new DateRangeEnd(bound);
        return ok;
    }
}

/// <summary>
/// The exclusive upper bound of a half-open window (the store compares <c>&lt;</c>): a date-only value
/// ends at the next day's 00:00 UTC, so it also includes that whole UTC day.
/// </summary>
internal readonly record struct DateRangeExclusiveEnd(DateTimeOffset Value)
{
    public static bool TryParse(string? value, IFormatProvider? provider, out DateRangeExclusiveEnd result)
    {
        var ok = DateRangeBound.TryParseExclusiveEnd(value, out var bound);
        result = new DateRangeExclusiveEnd(bound);
        return ok;
    }
}

/// <summary>
/// Reads an optional bound as the instant the stores compare against, <c>null</c> when the query string
/// omits it. The value is already at offset zero (<see cref="DateRangeBound"/>).
/// </summary>
internal static class DateRangeQueryBoundExtensions
{
    public static DateTimeOffset? ToUtcInstant(this DateRangeStart? bound) => bound is { } b ? b.Value : null;

    public static DateTimeOffset? ToUtcInstant(this DateRangeEnd? bound) => bound is { } b ? b.Value : null;

    public static DateTimeOffset? ToUtcInstant(this DateRangeExclusiveEnd? bound) => bound is { } b ? b.Value : null;
}
