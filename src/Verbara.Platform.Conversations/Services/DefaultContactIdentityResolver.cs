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

    public async Task<Contact> ResolveAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct)
    {
        var existing = await _contactStore.FindByAddressAsync(tenantId, address, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            CreatedAt = _clock.UtcNow,
        };
        contact.AddAddress(address);

        await _contactStore.SaveAsync(contact, ct).ConfigureAwait(false);
        return contact;
    }
}
