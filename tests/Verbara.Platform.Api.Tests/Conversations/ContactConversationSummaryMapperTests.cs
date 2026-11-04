using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// contact-history-opens-by-assignment task 2.3 (design D6, D8; fixture <c>contact-conversation-history.v1.json</c>
/// <c>fields</c>): the rules the summary applies in C# to a stored row, the duration, <c>lastMessageAt</c>, the preview
/// and the members that are absent when unknown.
/// </summary>
public sealed class ContactConversationSummaryMapperTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 28, 9, 15, 0, TimeSpan.Zero);

    private static Conversation NewConversation(
        ConversationState state = ConversationState.Closed,
        DateTimeOffset? closedAt = null,
        string? closedReason = null,
        ConversationOwner? owner = null,
        DateTimeOffset? lastMessageAtColumn = null)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From("c-1"),
            TenantId = new TenantId("t-1"),
            ContactId = EntityId.From("k-1"),
            Channel = ChannelType.WhatsApp,
            State = state,
            Owner = owner,
            CreatedAt = Created,
            ClosedAt = closedAt,
            LastMessageAt = lastMessageAtColumn,
        };
        if (closedReason is not null)
            conversation.SetMetadata(ConversationCloseReasons.MetadataKey, closedReason);
        return conversation;
    }

    private static ConversationActivityMessage Text(string text, DateTimeOffset at, MessageDirection direction = MessageDirection.Inbound) =>
        new(direction, new MessageEnvelope([new TextBlock(text)]), at);

    // ── durationSeconds ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(ConversationCloseReasons.Inactivity)]
    [InlineData(ConversationCloseReasons.InactivityBacklog)]
    [InlineData(ConversationCloseReasons.CustomerReturned)]
    public void DurationSeconds_ShouldMeasureUpToTheLastMessage_WhenASystemCloseReasonIsRecorded(string reason)
    {
        // The fixture's second item: 09:15:00 → 10:20:05 is 3905 s; closedAt three days later would measure the silence.
        var conversation = NewConversation(closedAt: Created.AddDays(3).AddHours(1).AddSeconds(5), closedReason: reason);

        ContactConversationSummaryMapper.DurationSeconds(conversation, Created.AddSeconds(3905)).Should().Be(3905);
    }

    [Fact]
    public void DurationSeconds_ShouldMeasureUpToTheClose_WhenAPersonClosedIt()
    {
        var conversation = NewConversation(closedAt: Created.AddMinutes(30));

        ContactConversationSummaryMapper.DurationSeconds(conversation, Created.AddSeconds(72)).Should().Be(1800);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ConversationCloseReasons.Inactivity)]
    public void DurationSeconds_ShouldBeAbsent_WhenTheConversationHasNoCloseTime(string? reason)
    {
        var conversation = NewConversation(ConversationState.Active, closedReason: reason);

        ContactConversationSummaryMapper.DurationSeconds(conversation, Created.AddMinutes(5)).Should().BeNull();
    }

    [Fact]
    public void DurationSeconds_ShouldFloorToWholeSeconds_WhenTheSpanHasAFraction()
    {
        var conversation = NewConversation(closedAt: Created.AddMilliseconds(1999));

        ContactConversationSummaryMapper.DurationSeconds(conversation, Created).Should().Be(1);
    }

    [Fact]
    public void DurationSeconds_ShouldNeverBeNegative_WhenTheEndIsBeforeTheCreation()
    {
        var conversation = NewConversation(closedAt: Created.AddSeconds(-10));

        ContactConversationSummaryMapper.DurationSeconds(conversation, Created).Should().Be(0);
    }

    // ── lastMessageAt ─────────────────────────────────────────────────────────

    [Fact]
    public void LastMessageAt_ShouldBeTheActivityMessagesTime_WhenTheStoredClockIsThePre026Backfill()
    {
        // Migration 026 gave a terminal row its created_at; the newest activity message is 72 s later.
        var summary = new ContactConversationSummary(
            NewConversation(ConversationState.Abandoned, Created.AddMinutes(30), lastMessageAtColumn: Created),
            null, null, null, Text("hello", Created.AddSeconds(72)));

        ContactConversationSummaryMapper.LastMessageAt(summary).Should().Be(Created.AddSeconds(72));
    }

    [Fact]
    public void LastMessageAt_ShouldBeTheStoredClock_WhenThereIsNoActivityMessage()
    {
        var summary = new ContactConversationSummary(
            NewConversation(lastMessageAtColumn: Created.AddMinutes(9)), null, null, null, null);

        ContactConversationSummaryMapper.LastMessageAt(summary).Should().Be(Created.AddMinutes(9));
    }

    [Fact]
    public void LastMessageAt_ShouldBeTheCreation_WhenThereIsNeitherAMessageNorAStoredClock()
    {
        var summary = new ContactConversationSummary(NewConversation(), null, null, null, null);

        ContactConversationSummaryMapper.LastMessageAt(summary).Should().Be(Created);
    }

    // ── preview ───────────────────────────────────────────────────────────────

    [Fact]
    public void Preview_ShouldTrimTheText_WhenItHasSurroundingWhitespace()
    {
        ContactConversationSummaryMapper.Preview(new MessageEnvelope([new TextBlock("  \n hello there \t ")]))
            .Should().Be("hello there");
    }

    [Fact]
    public void Preview_ShouldCutTo160Characters_WhenTheTextIsLonger()
    {
        var text = new string('a', 150) + new string('b', 50);

        ContactConversationSummaryMapper.Preview(new MessageEnvelope([new TextBlock(text)]))
            .Should().Be(new string('a', 150) + new string('b', 10));
    }

    [Fact]
    public void Preview_ShouldNotSplitASurrogatePair_WhenThe160thCodeUnitIsAHighSurrogate()
    {
        // "😀" is two UTF-16 code units; at index 159 it would straddle the cut.
        var text = new string('a', 159) + "😀" + "tail";

        var preview = ContactConversationSummaryMapper.Preview(new MessageEnvelope([new TextBlock(text)]));

        preview.Should().Be(new string('a', 159), "a cut that would leave half a pair drops the whole pair");
        char.IsHighSurrogate(preview[^1]).Should().BeFalse();
    }

    [Fact]
    public void Preview_ShouldKeepAWholePair_WhenItEndsExactlyAtTheCut()
    {
        var text = new string('a', 158) + "😀" + "tail";

        ContactConversationSummaryMapper.Preview(new MessageEnvelope([new TextBlock(text)]))
            .Should().Be(new string('a', 158) + "😀");
    }

    [Fact]
    public void Preview_ShouldBeEmpty_WhenTheFirstBlockIsNotText()
    {
        ContactConversationSummaryMapper.Preview(new MessageEnvelope(
            [new ImageBlock("https://media.example/x.png", "a caption", "image/png"), new TextBlock("later text")]))
            .Should().BeEmpty("the preview is the first block's text, and an image has none (design D6)");
    }

    // ── the whole item ────────────────────────────────────────────────────────

    [Fact]
    public void ToDto_ShouldWriteEveryMember_WhenTheRowCarriesThemAll()
    {
        var conversation = NewConversation(
            closedAt: Created.AddDays(3), closedReason: ConversationCloseReasons.Inactivity,
            owner: ConversationOwner.ForAgent(EntityId.From("agent-a")));
        conversation.QueueId = EntityId.From("q-1");
        var summary = new ContactConversationSummary(
            conversation, "Support", "Agent A",
            new ConversationDispositionRef(EntityId.From("n-1"), EntityId.From("s-1"), 2),
            Text("  Your card was ordered.  ", Created.AddSeconds(3905), MessageDirection.Outbound));

        var dto = ContactConversationSummaryMapper.ToDto(summary, heldByCaller: false, dispositionLabel: "Card");

        dto.Should().Be(new ContactConversationSummaryDto(
            "c-1", ChannelType.WhatsApp, ConversationState.Closed, Created, Created.AddDays(3), 3905,
            ConversationCloseReasons.Inactivity, "q-1", "Support", "agent-a", "Agent A",
            new ConversationDispositionDto("n-1", "Card"), Created.AddSeconds(3905),
            new ConversationLastMessageDto(MessageDirection.Outbound, MessageBlockType.Text, "Your card was ordered.", Created.AddSeconds(3905)),
            false));
    }

    [Fact]
    public void ToDto_ShouldOmitEveryUnknownMember_WhenTheRowCarriesNone()
    {
        // An open conversation owned by a queue: no close, no agent, no queue recorded, never typified, no message.
        var summary = new ContactConversationSummary(
            NewConversation(ConversationState.Queued, owner: ConversationOwner.ForBot(EntityId.From("bot-1"))),
            null, null, null, null);

        var json = JsonNode.Parse(JsonSerializer.Serialize(
            ContactConversationSummaryMapper.ToDto(summary, heldByCaller: true, dispositionLabel: null),
            ApiJsonContext.Default.ContactConversationSummaryDto))!.AsObject();

        json.Select(member => member.Key).Should().BeEquivalentTo(
            ["id", "channel", "state", "createdAt", "lastMessageAt", "heldByCaller"],
            "a member without a value is absent, never null (spec), and a bot owner is no agent (design D8)");
        json["heldByCaller"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void ToDto_ShouldKeepTheNodeIdAndOmitTheLabel_WhenTheNodeNoLongerResolves()
    {
        var summary = new ContactConversationSummary(
            NewConversation(closedAt: Created.AddMinutes(1)), null, null,
            new ConversationDispositionRef(EntityId.From("n-gone"), EntityId.From("s-1"), 1), null);

        var json = JsonNode.Parse(JsonSerializer.Serialize(
            ContactConversationSummaryMapper.ToDto(summary, heldByCaller: false, dispositionLabel: null),
            ApiJsonContext.Default.ContactConversationSummaryDto))!;

        JsonNode.DeepEquals(json["disposition"], new JsonObject { ["nodeId"] = "n-gone" }).Should().BeTrue();
    }

    [Fact]
    public void ToDto_ShouldDescribeTheFirstBlock_WhenTheLastMessageIsAnImage()
    {
        var summary = new ContactConversationSummary(
            NewConversation(ConversationState.Abandoned, Created.AddMinutes(30)), null, null, null,
            new ConversationActivityMessage(MessageDirection.Inbound,
                new MessageEnvelope([new ImageBlock("https://media.example/x.png", null, "image/png")]), Created.AddSeconds(72)));

        var dto = ContactConversationSummaryMapper.ToDto(summary, heldByCaller: false, dispositionLabel: null);

        dto.LastMessage.Should().Be(new ConversationLastMessageDto(
            MessageDirection.Inbound, MessageBlockType.Image, string.Empty, Created.AddSeconds(72)));
        dto.DurationSeconds.Should().Be(1800, "no system close reason: closedAt - createdAt");
    }
}
