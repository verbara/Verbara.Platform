using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

/// <summary>
/// The session address a WebChat conversation records in its metadata: the session it was created for, or opened by.
/// One contact can hold the addresses of several sessions, so the conversation, not the contact, says where an agent's
/// reply goes. The underscore marks a system key, as <c>_offeredAt</c>.
/// </summary>
public static class WebChatConversationMetadata
{
    /// <summary>The metadata key of the session address.</summary>
    public const string SessionAddressKey = "_webchatSessionAddress";

    /// <summary>
    /// The session address <paramref name="conversation"/> records, or <c>null</c> when it has none to use: another
    /// channel, a conversation from before the address was recorded, or a value that cannot be a destination.
    /// </summary>
    public static ChannelAddress? SessionAddressOf(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Channel != ChannelType.WebChat
            || !conversation.Metadata.TryGetValue(SessionAddressKey, out var recorded)
            || string.IsNullOrWhiteSpace(recorded))
        {
            return null;
        }

        var address = new ChannelAddress(ChannelType.WebChat, recorded);
        return address.IsWebChatIdentityKey() ? null : address;
    }
}
