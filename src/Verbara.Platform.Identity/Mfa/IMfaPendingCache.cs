namespace Verbara.Platform.Identity.Mfa;

/// <summary>
/// Transient store for the MFA challenges issued during login and the OIDC callback.
/// </summary>
/// <remarks>
/// <para>
/// A challenge allows <see cref="MfaChallenge.MaxAttempts"/> attempts. Every attempt is reserved with
/// <see cref="TryBeginAttemptAsync"/> <em>before</em> the factor is checked, so racing requests can never
/// evaluate more factors than that, whichever replica serves them. The reservation that reaches the
/// limit removes the challenge in the same atomic step (<see cref="MfaChallengeAttempt.IsFinal"/>); a
/// correct factor on any earlier attempt removes it with <see cref="TryConsumeAsync"/>, and only the
/// caller that removed it may issue a session. A removed or expired challenge is unknown from then on.
/// </para>
/// <para>
/// The trade: a stolen challenge token gives up to three guesses within its five-minute lifetime instead
/// of one. That is accepted because the caller counts every attempt toward the account lockout before it
/// evaluates the factor, so the lockout bounds the guesses against an account across all of its
/// outstanding challenges, concurrent ones included. This cache bounds one challenge; the account bound
/// is the caller's.
/// </para>
/// <para>
/// Implementations MUST behave identically (one contract test suite runs against each) and MUST NOT throw
/// for a <see langword="null"/>, empty, unknown or expired token: those return <see langword="null"/> /
/// <see langword="false"/>.
/// </para>
/// </remarks>
public interface IMfaPendingCache
{
    /// <summary>
    /// Stores <paramref name="entry"/> under <paramref name="challengeToken"/>, replacing any challenge
    /// already stored there, with no attempt used.
    /// </summary>
    ValueTask StoreAsync(string challengeToken, MfaPendingEntry entry, CancellationToken ct);

    /// <summary>
    /// Atomically reserves the next attempt on a live challenge and returns its entry with the attempt
    /// number. The reservation that reaches <see cref="MfaChallenge.MaxAttempts"/> removes the challenge and
    /// is returned with <see cref="MfaChallengeAttempt.IsFinal"/> set. Returns <see langword="null"/> when the
    /// token names no live challenge (unknown, empty, expired, consumed or exhausted).
    /// </summary>
    ValueTask<MfaChallengeAttempt?> TryBeginAttemptAsync(string? challengeToken, CancellationToken ct);

    /// <summary>
    /// Removes a live challenge. Returns <see langword="true"/> only for the caller that removed it; of
    /// racing callers exactly one wins.
    /// </summary>
    ValueTask<bool> TryConsumeAsync(string? challengeToken, CancellationToken ct);
}

/// <summary>The attempt rule shared by every <see cref="IMfaPendingCache"/>.</summary>
public static class MfaChallenge
{
    /// <summary>Factors one challenge may evaluate before it is spent.</summary>
    public const int MaxAttempts = 3;
}

/// <summary>
/// One reserved attempt on a challenge: its entry, the attempt number (1-based) and whether this attempt
/// was the last one, in which case the challenge has already been removed.
/// </summary>
public sealed record MfaChallengeAttempt(MfaPendingEntry Entry, int Attempt, bool IsFinal);

public sealed class MfaPendingEntry
{
    public required string UserId { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
