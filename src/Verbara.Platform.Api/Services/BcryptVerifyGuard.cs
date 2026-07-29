namespace Verbara.Platform.Api.Services;

/// <summary>
/// The single guarded entry point for verifying a plaintext against a <b>stored</b> BCrypt digest.
/// </summary>
/// <remarks>
/// <para>
/// Platform/ADR-0013 makes "a crypto-library parse failure on stored material is a verify failure,
/// never an error path" a standing requirement on every stored-credential verifier. Honouring it with
/// a bare <c>catch (SaltParseException)</c> is <b>not sufficient</b>: BCrypt.Net-Next raises that type
/// only when the stored value does not begin with <c>$</c>. A value that is corrupt <i>inside</i> the
/// BCrypt family — a truncated digest, a non-numeric cost factor, a bare <c>"$2"</c> — raises
/// <see cref="IndexOutOfRangeException"/>, <see cref="FormatException"/> or
/// <see cref="ArgumentOutOfRangeException"/> instead. Two of those land in
/// <c>ErrorHandlingMiddleware</c>'s <c>_</c> arm as <b>HTTP 500</b>, leaking a cryptography library's
/// message through <c>ProblemDetails.Detail</c>.
/// </para>
/// <para>
/// Every exception listed here is measured, not assumed — see
/// <c>MfaServiceTests.ValidateRecoveryCode_ShouldReturnFalse_WhenStoredDigestIsCorruptInsideTheBcryptFamily</c>
/// and <c>PasswordServiceTests.VerifyPassword_ShouldReturnFalse_WhenStoredHashIsCorruptInsideTheBcryptFamily</c>,
/// which pin the behaviour against the real library.
/// </para>
/// <para>
/// The filter is deliberately narrow. It never swallows an <see cref="OperationCanceledException"/>,
/// an <see cref="OutOfMemoryException"/>, or any other exception unrelated to parsing the stored
/// digest — those still propagate.
/// </para>
/// </remarks>
internal static class BcryptVerifyGuard
{
    /// <summary>
    /// Verifies <paramref name="plaintext"/> against <paramref name="storedHash"/>, treating any
    /// failure to parse the stored digest as "no match" rather than letting it raise.
    /// </summary>
    /// <param name="plaintext">The caller-supplied secret.</param>
    /// <param name="storedHash">The digest as read from storage — untrusted in shape.</param>
    /// <returns><see langword="true"/> only on a positive verification; <see langword="false"/> for a
    /// mismatch AND for any unparseable stored digest. Fail-closed by construction.</returns>
    internal static bool SafeVerify(string plaintext, string storedHash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(plaintext, storedHash);
        }
        catch (Exception ex) when (IsStoredDigestParseFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> is BCrypt.Net-Next failing to parse the stored digest.
    /// <see cref="ArgumentException"/> covers <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    private static bool IsStoredDigestParseFailure(Exception ex) =>
        ex is BCrypt.Net.SaltParseException
           or ArgumentException
           or FormatException
           or IndexOutOfRangeException;
}
