namespace Verbara.Platform.Identity.Mfa;

/// <summary>
/// How many users hold recovery codes, split by whether any of their stored digests is still in a
/// legacy family (see <see cref="RecoveryCodeFamilies"/>).
/// </summary>
/// <param name="UsersOnArgon2id">Users whose every stored digest is Argon2id.</param>
/// <param name="UsersOnLegacy">Users holding at least one BCrypt or salted SHA-256 digest.</param>
public sealed record RecoveryCodeCensus(long UsersOnArgon2id, long UsersOnLegacy);

/// <summary>
/// Counts the users on each recovery-code family across every tenant, for the operator gauge
/// <c>mfa.recovery_codes.users{family}</c>.
/// </summary>
/// <remarks>
/// <para>
/// A user counts once their MFA is on and they hold a non-null, non-empty array (a pending, unconfirmed
/// enrollment is not counted). The family of a digest is invisible to
/// SQL (the column is DataProtection-wrapped per element), so an implementation reads and unwraps
/// every array and classifies it with <see cref="RecoveryCodeFamilies.HoldsLegacy"/>. That is a full
/// scan: callers run it on a slow timer, never per request.
/// </para>
/// <para>
/// The result carries no tenant, user or digest: the gauge is exported on an unauthenticated
/// <c>/metrics</c> endpoint.
/// </para>
/// </remarks>
public interface IRecoveryCodeCensus
{
    /// <summary>Scan every user and count them per family.</summary>
    Task<RecoveryCodeCensus> CountAsync(CancellationToken ct);
}
