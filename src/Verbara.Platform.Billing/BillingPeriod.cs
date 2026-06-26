using System.Globalization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

/// <summary>
/// Canonical UTC calendar-month billing period. Single source of truth for the
/// <c>[firstOfMonthUtc, firstOfNextMonthUtc)</c> boundary that quota enforcement, the credit meter, the
/// credits readout, and the metering summary all compute. Extracted from four previously-inlined,
/// byte-identical <c>GetCurrentPeriod()</c> copies (ADR-0033 / the credit-ledger-substrate spec delta) so
/// every site agrees and the subscription-grant mint can key on the same <c>"yyyy-MM"</c> period key.
/// This helper changes no boundary — it only removes duplication.
/// </summary>
public static class BillingPeriod
{
    /// <summary>
    /// Returns the current UTC calendar-month period for the supplied clock:
    /// <paramref name="clock"/>'s <see cref="IClock.UtcNow"/> month start (inclusive), next month start
    /// (exclusive), and the <c>"yyyy-MM"</c> period key.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End, string Key) Current(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var key = string.Create(CultureInfo.InvariantCulture, $"{now.Year:D4}-{now.Month:D2}");
        return (start, start.AddMonths(1), key);
    }
}
