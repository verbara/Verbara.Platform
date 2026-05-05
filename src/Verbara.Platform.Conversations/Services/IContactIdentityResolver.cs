using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Services;

public interface IContactIdentityResolver
{
    Task<Contact> ResolveAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct);
}
