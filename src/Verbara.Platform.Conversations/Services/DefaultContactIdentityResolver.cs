using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Services;

public sealed class DefaultContactIdentityResolver : IContactIdentityResolver
{
    private readonly IContactStore _contactStore;
    private readonly IClock _clock;

    public DefaultContactIdentityResolver(IContactStore contactStore, IClock clock)
    {
        _contactStore = contactStore;
        _clock = clock;
    }

    public Task<Contact> ResolveAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct) =>
        ResolveAsync(tenantId, new ContactIdentity(address, [], null), ct);

    /// <summary>
    /// Finds the contact by <see cref="ContactIdentity.Address"/>, then by each alternate address in order; a found
    /// contact gains every address of the identity it lacked, and the display name when it has no name. When none
    /// matches, one contact is created holding all of them. The contact is written only when something changed.
    /// </summary>
    public async Task<Contact> ResolveAsync(TenantId tenantId, ContactIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ChannelAddress[] addresses = [identity.Address, .. identity.AlternateAddresses];

        Contact? existing = null;
        ChannelAddress? matched = null;
        foreach (var address in addresses)
        {
            existing = await _contactStore.FindByAddressAsync(tenantId, address, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                matched = address;
                break;
            }
        }

        var contact = existing ?? new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            CreatedAt = _clock.UtcNow,
        };

        var changed = existing is null;
        // The address the store matched is the contact's by definition; only the others can be new.
        foreach (var address in addresses.Where(a => a != matched && !contact.Addresses.Contains(a)))
        {
            contact.AddAddress(address);
            changed = true;
        }

        if (contact.FirstName is null && contact.LastName is null && !string.IsNullOrWhiteSpace(identity.DisplayName))
        {
            contact.FirstName = identity.DisplayName.Trim();
            changed = true;
        }

        if (!changed)
            return contact;

        if (existing is not null)
            contact.UpdatedAt = _clock.UtcNow;

        await _contactStore.SaveAsync(contact, ct).ConfigureAwait(false);
        return contact;
    }
}
