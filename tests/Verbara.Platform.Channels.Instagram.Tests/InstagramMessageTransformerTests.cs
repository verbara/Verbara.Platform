using Verbara.Platform.Channels.Instagram;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Instagram.Tests;

public class InstagramMessageTransformerTests
{
    private static readonly InstagramMessageTransformer Transformer = new();

    // ── TargetChannel ─────────────────────────────────────────────────────────

    [Fact]
    public void TargetChannel_ShouldBeInstagram()
    {
        Transformer.TargetChannel.Should().Be(ChannelType.Instagram);
    }

    // ── TextBlock passthrough ─────────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldReturnSameText_WhenWithinLimit()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello Instagram!")]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().Be("Hello Instagram!");
    }

    // ── TextBlock truncation (Instagram limit 1000) ───────────────────────────

    [Fact]
    public void Transform_ShouldTruncateText_WhenExceedsInstagramLimit()
    {
        var longText = new string('x', 1500);
        var envelope = new MessageEnvelope([new TextBlock(longText)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Length.Should().Be(1000);
    }

    // ── InteractiveBlock → quick replies (no buttons on Instagram) ────────────

    [Fact]
    public void Transform_ShouldMapInteractiveBlockToQuickReplies_WhenWithinLimits()
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
        block.Body.Should().Be("Choose:");
    }

    // ── InteractiveBlock title truncation ─────────────────────────────────────

    [Fact]
    public void Transform_ShouldTruncateQuickReplyTitles_WhenExceedingLimit()
    {
        var replies = new List<QuickReply>
        {
            new("id1", "This is a very long quick reply title that should be truncated"),
        };
        var envelope = new MessageEnvelope([new InteractiveBlock("Body", replies)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies[0].Title.Length.Should().Be(20);
    }
}
