namespace Asterisk.Platform.Identity.Mfa;

/// <summary>
/// Transient store for password-reset tokens issued by <c>POST /auth/forgot-password</c>.
/// <c>TakeAsync</c> removes the entry atomically — a token can only be consumed once.
/// </summary>
public interface IPasswordResetCache
{
    ValueTask StoreAsync(string resetToken, PasswordResetEntry entry, CancellationToken ct);

    /// <summary>
    /// Retrieves and removes the entry for <paramref name="resetToken"/>.
    /// Returns <see langword="null"/> when the token is unknown or has expired.
    /// </summary>
    ValueTask<PasswordResetEntry?> TakeAsync(string resetToken, CancellationToken ct);
}

public sealed class PasswordResetEntry
{
    public required string UserId { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
