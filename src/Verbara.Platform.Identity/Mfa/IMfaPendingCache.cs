namespace Verbara.Platform.Identity.Mfa;

/// <summary>
/// Transient store for MFA challenge tokens issued during login and OIDC callback.
/// <c>TakeAsync</c> removes the entry atomically — a token can only be consumed once.
/// </summary>
public interface IMfaPendingCache
{
    ValueTask StoreAsync(string challengeToken, MfaPendingEntry entry, CancellationToken ct);

    /// <summary>
    /// Retrieves and removes the entry for <paramref name="challengeToken"/>.
    /// Returns <see langword="null"/> when the token is unknown or has expired.
    /// </summary>
    ValueTask<MfaPendingEntry?> TakeAsync(string challengeToken, CancellationToken ct);
}

public sealed class MfaPendingEntry
{
    public required string UserId { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
