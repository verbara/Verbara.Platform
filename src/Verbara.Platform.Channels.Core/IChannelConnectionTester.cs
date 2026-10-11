using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// Checks a tenant's channel configuration against the provider (whatsapp-works-for-real D15). A channel
/// module registers one only when it can make a real call; a channel without a tester is reported
/// <see cref="ChannelConnectionTestCodes.NotTestable"/> and never as a success.
/// </summary>
public interface IChannelConnectionTester
{
    ChannelType Channel { get; }

    /// <summary>Tests the tenant's stored configuration. Never throws for a provider failure.</summary>
    Task<ChannelConnectionTestResult> TestAsync(TenantId tenantId, CancellationToken ct);
}

/// <summary>The outcome of a connection test.</summary>
/// <param name="Success">Whether the provider accepted the configuration.</param>
/// <param name="Message">Human-readable outcome; on a provider rejection, the provider's own message.</param>
/// <param name="Code">A <see cref="ChannelConnectionTestCodes"/> value on failure; null on success.</param>
/// <param name="VerifiedName">The provider's display name for the account, when it reports one.</param>
public sealed record ChannelConnectionTestResult(bool Success, string Message, string? Code = null, string? VerifiedName = null);

/// <summary>Machine codes of a failed connection test.</summary>
public static class ChannelConnectionTestCodes
{
    /// <summary>The tenant lacks a credential the test needs; nothing was sent.</summary>
    public const string ChannelNotConfigured = "channel-not-configured";

    /// <summary>The channel has no connection test.</summary>
    public const string NotTestable = "not-testable";

    /// <summary>The provider answered with an error; the message is the provider's.</summary>
    public const string ProviderRejected = "provider-rejected";

    /// <summary>The provider could not be reached (network failure or timeout).</summary>
    public const string ProviderUnreachable = "provider-unreachable";
}
