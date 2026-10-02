using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Core.Push;

/// <summary>
/// Closes a live connection — a Realtime hub connection, an Api SSE stream — no later than the
/// credential that opened it expires.
/// </summary>
/// <remarks>
/// <para>
/// Such a connection is authenticated once, when it opens, and nothing checks its credential
/// again. Unbounded, it would outlive that credential for as long as the client keeps it open, and
/// with it anything that should have ended it but did not reach it: a
/// <see cref="UserAccessRevokedEvent"/> this node missed, an account status read from a stale
/// cache, sessions revoked for an account that is still active, an API key revoked or rotated.
/// Closed at the credential's expiry, each of those is bounded by the credential's lifetime.
/// </para>
/// <para>
/// The close must let the client reconnect: it comes back with the credential it holds by then
/// (the Web app refreshes its access token before the old one expires), which authentication and
/// the account-status checks judge afresh. A refusal is for an account that may no longer connect,
/// not for a token that ran out.
/// </para>
/// </remarks>
public static partial class LiveConnectionExpiry
{
    /// <summary>
    /// The claim a principal's expiry is read from (Unix seconds): <c>exp</c>, as in every JWT
    /// Platform.Api issues. Platform.Api's API-key handler stamps one on its principals too.
    /// </summary>
    public const string ClaimType = "exp";

    // The latest instant DateTimeOffset can hold, in Unix seconds (9999-12-31T23:59:59Z).
    private const long MaxUnixSeconds = 253_402_300_799;

    // The longest due time a timer accepts (0xFFFFFFFE ms, about 49.7 days).
    private static readonly TimeSpan MaxDueTime = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// When the credential behind <paramref name="principal"/> expires: its <see cref="ClaimType"/>
    /// claim. A principal without a readable one cannot be bounded, so it counts as already expired.
    /// </summary>
    public static DateTimeOffset Of(ClaimsPrincipal? principal) =>
        long.TryParse(principal?.FindFirst(ClaimType)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;

    /// <summary>
    /// Runs <paramref name="close"/> once <paramref name="expiresAt"/> is reached — at once if it
    /// already has — on a timer thread. Dispose the returned timer when the connection ends first.
    /// </summary>
    /// <param name="time">The clock the expiry is measured against.</param>
    /// <param name="expiresAt">The credential's expiry, from <see cref="Of"/>.</param>
    /// <param name="close">
    /// Ends the connection in a way the client may reconnect from. An
    /// <see cref="ObjectDisposedException"/> from it means the connection is already gone.
    /// </param>
    /// <param name="logger">Where any other failure of <paramref name="close"/> is reported.</param>
    /// <remarks>
    /// An expiry further out than a timer can be armed closes the connection at that horizon
    /// instead: early, never late.
    /// </remarks>
    public static ITimer ScheduleClose(TimeProvider time, DateTimeOffset expiresAt, Action close, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(logger);

        var due = expiresAt - time.GetUtcNow();
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;
        else if (due > MaxDueTime)
            due = MaxDueTime;

        return time.CreateTimer(_ => CloseContained(close, logger), state: null, dueTime: due, period: Timeout.InfiniteTimeSpan);
    }

    // Runs on a timer thread, where an escaping exception would take the process down.
    private static void CloseContained(Action close, ILogger logger)
    {
        try
        {
            close();
        }
        catch (ObjectDisposedException)
        {
            // The connection finished tearing down meanwhile: it is gone, which is what the close wanted.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCloseFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 7302, Level = LogLevel.Error,
        Message = "Failed to close a live connection when the credential that opened it expired.")]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);
}
