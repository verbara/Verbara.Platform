using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// Implemented by a connector whose destinations are live sessions, not addresses a provider accepts at any time. It
/// answers which of a contact's addresses has a visitor connected at this moment, so a conversation an agent starts can
/// go to a session that will receive it.
/// </summary>
public interface IConnectedSessionDirectory
{
    /// <summary>
    /// The contact's newest address whose session is connected on this replica, or <c>null</c> when none is.
    /// </summary>
    Task<ChannelAddress?> FindNewestConnectedAddressAsync(Contact contact, CancellationToken ct);
}
