namespace Asterisk.Platform.Identity.Auth.Jwt;

/// <summary>
/// Options controlling JWT signing-key rotation cadence + key strength.
/// </summary>
/// <remarks>
/// R5.4 S5.9 — Bound from configuration section <c>Identity:JwtKeyRotation</c>.
/// Defaults err on the side of strong (64-byte / 512-bit material) keys with a
/// 24h active window + 24h grace so in-flight tokens never see a hard cutover.
/// </remarks>
public sealed class JwtKeyRotationOptions
{
    /// <summary>Length (bytes) of newly-minted symmetric key material. Default 64 (512-bit).</summary>
    public int KeySizeBytes { get; set; } = 64;

    /// <summary>How long a freshly-rotated key remains the signing key before being demoted by the next rotation. Default 24h.</summary>
    public TimeSpan ActiveDuration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Additional time after <see cref="ActiveDuration"/> during which a demoted
    /// key still validates already-issued tokens. Default 24h.
    /// </summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromHours(24);
}
