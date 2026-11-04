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
    /// The <see cref="Contact.UpdatedBy"/> of a write the resolver makes to an existing contact: the actor id the
    /// platform records for actions no user performed (the audit trail's <c>"system"</c> actor).
    /// </summary>
    public const string SystemActorId = "system";

    /// <inheritdoc />
    public Task<Contact?> FindByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) =>
        _contactStore.GetByIdAsync(tenantId, contactId, ct);

    /// <summary>
    /// Finds the contact by <see cref="ContactIdentity.Address"/>, then by each alternate address in order; a found
    /// contact gains every address of the identity it lacked, and the display name when it has no name. When none
    /// matches, one contact is created holding all of them. A found contact is written only when something changed,
    /// and only through <see cref="IContactStore.AddIdentityAsync"/>: the rest of the row is never rewritten from
    /// this read, and a contact erased since the read is not brought back — the sender then becomes a new contact
    /// holding only this message's identity, as if the message had arrived after the erasure.
    /// </summary>
    public async Task<Contact> ResolveAsync(TenantId tenantId, ContactIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ChannelAddress[] addresses = [identity.Address, .. identity.AlternateAddresses];
        var displayName = string.IsNullOrWhiteSpace(identity.DisplayName) ? null : identity.DisplayName.Trim();

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

        if (existing is not null)
        {
            // The address the store matched is the contact's by definition; only the others can be new.
            var missing = addresses.Where(a => a != matched && !existing.Addresses.Contains(a)).Distinct().ToArray();
            var nameToFill = existing.FirstName is null && existing.LastName is null ? displayName : null;
            if (missing.Length == 0 && nameToFill is null)
                return existing;

            var updated = await _contactStore.AddIdentityAsync(
                tenantId, existing.ContactId, missing, nameToFill, _clock.UtcNow, SystemActorId, ct).ConfigureAwait(false);
            if (updated is not null)
                return updated;
        }

        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            FirstName = displayName,
            CreatedAt = _clock.UtcNow,
        };
        foreach (var address in addresses)
            contact.AddAddress(address);

        await _contactStore.SaveAsync(contact, ct).ConfigureAwait(false);
        return contact;
    }
}
