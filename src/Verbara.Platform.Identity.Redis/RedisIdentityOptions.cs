namespace Verbara.Platform.Identity.Redis;

/// <summary>
/// Configuration for Redis-backed <c>IMfaPendingCache</c> + <c>IPasswordResetCache</c> implementations.
/// </summary>
public sealed class RedisIdentityOptions
{
    /// <summary>
    /// StackExchange.Redis configuration string (e.g. <c>"localhost:6379"</c>). Required
    /// when no <see cref="StackExchange.Redis.IConnectionMultiplexer"/> has already been
    /// registered by the consumer (e.g. by another Pro.*.Redis package).
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Redis database index to use. Defaults to <c>-1</c> (the multiplexer's default
    /// database, typically <c>0</c>).
    /// </summary>
    public int DatabaseIndex { get; set; } = -1;

    /// <summary>
    /// Key prefix for all keys written by this package. Defaults to
    /// <c>"asterisk:identity:"</c> — final keys take the form
    /// <c>{prefix}mfa:pending:{challengeToken}</c> and
    /// <c>{prefix}mfa:passwordreset:{resetToken}</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "asterisk:identity:";
}
