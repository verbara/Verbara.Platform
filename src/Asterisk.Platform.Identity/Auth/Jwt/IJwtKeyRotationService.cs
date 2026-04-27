namespace Asterisk.Platform.Identity.Auth.Jwt;

/// <summary>
/// High-level service for managing the JWT signing-key rotation pool.
/// Consumers (token issuance + validation) should call this rather than
/// touching <see cref="IJwtKeyStore"/> directly.
/// </summary>
/// <remarks>
/// R5.4 S5.9 — Closes C.1 of post-R5.1 triage. Pairs the key store with
/// the configured rotation cadence + key-size policy in
/// <see cref="JwtKeyRotationOptions"/>.
/// </remarks>
public interface IJwtKeyRotationService
{
    /// <summary>
    /// Creates a new signing key, persists it as active, and demotes any
    /// previously-active key to validation-only state. Returns the new entry.
    /// </summary>
    Task<JwtKeyEntry> RotateAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the current active signing key, performing an automatic
    /// bootstrap rotation if the store is empty.
    /// </summary>
    Task<JwtKeyEntry> GetActiveSigningKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns every non-expired key (active + grace-window) for use in
    /// JWT validation parameters so historical tokens still verify.
    /// </summary>
    Task<IReadOnlyList<JwtKeyEntry>> GetValidationKeysAsync(CancellationToken ct = default);
}
