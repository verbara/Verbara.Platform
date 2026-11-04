using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// contact-history-opens-by-assignment task 2.8 (design D17 "Where each publisher gets its audience"): the one helper every
/// <c>conversation.message</c> publisher stamps its audience with. The owner when its kind is <c>Agent</c>, the agent of
/// an open offer, and nothing else.
/// </summary>
public sealed class ConversationOfferAudienceTests
{
    private static Conversation Conversation(ConversationOwner? owner, ConversationState state, string? offeredTo = null)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From("conv-1"),
            TenantId = new TenantId("tenant-1"),
            ContactId = EntityId.From("contact-1"),
            Channel = ChannelType.WhatsApp,
            State = state,
            Owner = owner,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        if (offeredTo is not null)
            conversation.SetMetadata(ConversationOffer.OfferedToKey, offeredTo);
        return conversation;
    }

    [Fact]
    public void AudienceOf_ShouldNameTheOwner_WhenTheOwnerIsAnAgent()
    {
        ConversationOffer.AudienceOf(Conversation(ConversationOwner.ForAgent(EntityId.From("agent-a")), ConversationState.Active))
            .Should().Be(new ConversationAudience("agent-a", null));
    }

    [Fact]
    public void AudienceOf_ShouldNameTheOfferedAgent_WhenTheConversationIsOpenOffered()
    {
        ConversationOffer.AudienceOf(Conversation(ConversationOwner.ForQueue(EntityId.From("q-1")), ConversationState.Offered, "agent-o"))
            .Should().Be(new ConversationAudience(null, "agent-o"));
    }

    [Theory]
    [InlineData(ConversationState.Queued)]
    [InlineData(ConversationState.Active)]
    public void AudienceOf_ShouldIgnoreAStaleOfferStamp_WhenTheConversationIsNoLongerOffered(ConversationState state)
    {
        // An offer that ended can leave its _offeredTo behind; only the Offered state opens it.
        ConversationOffer.AudienceOf(Conversation(ConversationOwner.ForQueue(EntityId.From("q-1")), state, "agent-o"))
            .Should().Be(ConversationAudience.None);
    }

    [Fact]
    public void AudienceOf_ShouldBeEmpty_WhenNoAgentOwnsOrIsOffered()
    {
        ConversationOffer.AudienceOf(null).Should().Be(ConversationAudience.None);
        ConversationOffer.AudienceOf(Conversation(null, ConversationState.Queued)).Should().Be(ConversationAudience.None);
        ConversationOffer.AudienceOf(Conversation(ConversationOwner.System, ConversationState.Active)).Should().Be(ConversationAudience.None);
        ConversationOffer.AudienceOf(Conversation(ConversationOwner.ForBot(EntityId.From("bot-1")), ConversationState.Active))
            .Should().Be(ConversationAudience.None);
        ConversationOffer.AudienceOf(Conversation(ConversationOwner.ForQueue(EntityId.From("q-1")), ConversationState.Offered, ""))
            .Should().Be(ConversationAudience.None, "an offer that records no agent is made to no one");
    }
}
