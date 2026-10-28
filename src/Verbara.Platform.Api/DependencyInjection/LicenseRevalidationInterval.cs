using System.Globalization;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// Turns <c>Licensing:RevalidationInterval</c> into the interval handed to
/// <c>LicenseOptions.RevalidationInterval</c>.
/// </summary>
/// <remarks>
/// The licence gate and the guard decide on the last validation result, and the periodic
/// revalidation is what moves a licence that runs out while the process is up to Expired. Pro
/// switches that timer off for an interval of zero, a negative one or
/// <see cref="Timeout.InfiniteTimeSpan"/>, so the host never passes one: an unreadable value, or one
/// that would switch revalidation off, falls back to <see cref="Default"/>, and a longer interval is
/// capped at <see cref="Maximum"/>. The result stays at most one day stale.
/// </remarks>
internal static class LicenseRevalidationInterval
{
    /// <summary>The interval used when none is configured, or the configured one is not usable.</summary>
    internal static readonly TimeSpan Default = TimeSpan.FromHours(6);

    /// <summary>The longest interval the host accepts.</summary>
    internal static readonly TimeSpan Maximum = TimeSpan.FromHours(24);

    internal static TimeSpan Resolve(string? configured)
    {
        if (!TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var interval)
            || interval <= TimeSpan.Zero)
        {
            return Default;
        }

        return interval > Maximum ? Maximum : interval;
    }
}
