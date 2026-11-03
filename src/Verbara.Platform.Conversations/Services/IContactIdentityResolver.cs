using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Services;

public interface IContactIdentityResolver
{
    Task<Contact> ResolveAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct);

    /// <summary>
    /// Resolves the contact for an inbound sender that may carry several addresses and a display name. The default
    /// resolves by <see cref="ContactIdentity.Address"/> alone.
    /// </summary>
    Task<Contact> ResolveAsync(TenantId tenantId, ContactIdentity identity, CancellationToken ct) =>
        ResolveAsync(tenantId, identity.Address, ct);
}

/// <summary>What an inbound delivery says about its sender.</summary>
/// <param name="Address">The address looked up first (on WhatsApp, the business-scoped user id when present).</param>
/// <param name="AlternateAddresses">Further addresses of the same sender, looked up in order when the first misses.</param>
/// <param name="DisplayName">The sender's provider profile name, used only when the contact has no name.</param>
public sealed record ContactIdentity(
    ChannelAddress Address,
    IReadOnlyList<ChannelAddress> AlternateAddresses,
    string? DisplayName);
