using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations.Services;

public interface IContactIdentityResolver
{
    Task<Contact> ResolveAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct);
}
