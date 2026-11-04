using Verbara.Platform.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.Core;

/// <summary>One customer message a provider webhook carried.</summary>
/// <param name="From">The sender's primary identity: the address the contact is looked up by first.</param>
/// <param name="Content">The message blocks.</param>
/// <param name="ExternalMessageId">The provider's message id (deduplication key).</param>
/// <param name="Timestamp">When the provider says the message was sent.</param>
public sealed record InboundMessage(
    ChannelAddress From,
    MessageEnvelope Content,
    string ExternalMessageId,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Other addresses of the same sender carried by the delivery, looked up after <see cref="From"/> and stored on
    /// the contact when missing — e.g. the WhatsApp phone number when <see cref="From"/> is the business-scoped user id.
    /// </summary>
    public IReadOnlyList<ChannelAddress> AlternateAddresses { get; init; } = [];

    /// <summary>The sender's display name as the provider reports it (WhatsApp <c>contacts[].profile.name</c>), if any.</summary>
    public string? ProfileName { get; init; }

    /// <summary>
    /// The WebChat session this message came from, or <c>null</c> on a provider webhook. A bound message is attributed
    /// to the session's contact and stays in the session's own conversation; a webhook message is resolved by address.
    /// </summary>
    public InboundSessionBinding? SessionBinding { get; init; }
}

/// <summary>What a WebChat session says about the message it sends.</summary>
/// <param name="ContactId">The contact the session is bound to.</param>
/// <param name="ConversationId">The conversation the session is bound to.</param>
/// <param name="SessionAddress">The session's address, which a conversation it opens records for its replies.</param>
public sealed record InboundSessionBinding(EntityId ContactId, EntityId ConversationId, ChannelAddress SessionAddress);
