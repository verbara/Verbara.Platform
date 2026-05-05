namespace Verbara.Platform.Identity;

/// <summary>
/// Configuration options for the identity subsystem.
/// </summary>
public sealed class IdentityOptions
{
    /// <summary>
    /// Default rate limit (requests per minute) for newly created API keys.
    /// </summary>
    public int DefaultApiKeyRateLimitPerMinute { get; set; } = 600;

    /// <summary>
    /// Default expiration period for newly created API keys.
    /// </summary>
    public TimeSpan DefaultApiKeyExpiration { get; set; } = TimeSpan.FromDays(365);
}
