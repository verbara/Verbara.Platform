using Verbara.Platform.Channels.Messenger;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Messenger.Tests;

public class MessengerMessageTransformerTests
{
    private static readonly MessengerMessageTransformer Transformer = new();

    // ── TargetChannel ─────────────────────────────────────────────────────────

    [Fact]
    public void TargetChannel_ShouldBeMessenger()
    {
        Transformer.TargetChannel.Should().Be(ChannelType.Messenger);
    }

    // ── TextBlock passthrough ─────────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldReturnSameEnvelope_WhenTextIsWithinLimit()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello!")]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().Be("Hello!");
    }

    // ── TextBlock truncation ──────────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldTruncateText_WhenExceedsMessengerLimit()
    {
        var longText = new string('x', 2500);
        var envelope = new MessageEnvelope([new TextBlock(longText)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Length.Should().Be(2000);
    }

    // ── InteractiveBlock → quick replies ──────────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThroughInteractiveBlock_WhenWithinLimits()
    {
        var replies = new List<QuickReply>
        {
            new("yes", "Yes"),
            new("no", "No"),
        };
        var envelope = new MessageEnvelope([new InteractiveBlock("Choose:", replies)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies.Should().HaveCount(2);
    }

    // ── InteractiveBlock capped at 13 ─────────────────────────────────────────

    [Fact]
    public void Transform_ShouldCapQuickReplies_WhenMoreThan13Provided()
    {
        var replies = Enumerable.Range(1, 20)
            .Select(i => new QuickReply($"id-{i}", $"Option {i}"))
            .ToList();
        var envelope = new MessageEnvelope([new InteractiveBlock("Pick one:", replies)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies.Should().HaveCount(13);
    }

    // ── InteractiveBlock title truncation ─────────────────────────────────────

    [Fact]
    public void Transform_ShouldTruncateQuickReplyTitles_WhenExceedingLimit()
    {
        var replies = new List<QuickReply>
        {
            new("id1", "This title is way too long for Messenger"),
        };
        var envelope = new MessageEnvelope([new InteractiveBlock("Body", replies)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies[0].Title.Length.Should().Be(20);
    }

    // ── Empty envelope passthrough ────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldReturnSameEnvelope_WhenEmpty()
    {
        var envelope = new MessageEnvelope([]);

        var result = Transformer.Transform(envelope);

        result.Blocks.Should().BeEmpty();
    }
}
