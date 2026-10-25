namespace Verbara.Platform.Typification.Stores;

/// <summary>
/// The dispositions stored typifications owe the dialer (design D16). A row is written by
/// <see cref="ITypificationSubmissionStore.TryInsertAsync"/> with its submission, pre-claimed by the request; this store
/// claims, completes and releases it. Every lease time is the caller's clock, never the database's, so the request's
/// lease and a dispatcher's claim are compared on one clock (as the outbound webhook claims, Platform/ADR-0043).
/// </summary>
public interface IDialerDispositionDispatchStore
{
    /// <summary>
    /// Claims at most <paramref name="limit"/> rows without a result that are due at <paramref name="now"/> and hold no
    /// live claim (none, or one whose lease passed), each with a fresh claim id expiring at
    /// <paramref name="expiresAt"/> and one more attempt, in one statement that skips rows another caller is claiming.
    /// </summary>
    /// <returns>The claimed rows as the claim left them (their <see cref="DialerDispositionDispatch.ClaimId"/> is the new claim).</returns>
    Task<IReadOnlyList<DialerDispositionDispatch>> ClaimDueAsync(
        int limit, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct);

    /// <summary>
    /// Records the dialer's answer on the row held by <paramref name="claimId"/>: sets the result and the completion
    /// time, the announcement time when <paramref name="published"/>, and clears the claim.
    /// </summary>
    /// <returns><see langword="false"/>, writing nothing, when another claim took the row over.</returns>
    Task<bool> CompleteAsync(
        DialerDispositionDispatch dispatch, string result, bool published, string claimId, CancellationToken ct);

    /// <summary>
    /// Gives back the row held by <paramref name="claimId"/> after a failed hand-off: keeps its attempts, records
    /// <paramref name="lastError"/>, makes it due at <paramref name="nextAttemptAt"/> and clears the claim.
    /// </summary>
    /// <returns><see langword="false"/>, writing nothing, when another claim took the row over.</returns>
    Task<bool> ReleaseAsync(
        DialerDispositionDispatch dispatch, string lastError, DateTimeOffset nextAttemptAt, string claimId, CancellationToken ct);

    /// <summary>How many rows have no result yet (the pending gauge).</summary>
    Task<long> CountPendingAsync(CancellationToken ct);
}
