using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WhatsApp.Tests;

public class WhatsAppMessageTransformerTests
{
    private static readonly WhatsAppMessageTransformer Transformer = new();

    // ── Channel property ──────────────────────────────────────────────────────

    [Fact]
    public void TargetChannel_ShouldBeWhatsApp()
    {
        Transformer.TargetChannel.Should().Be(ChannelType.WhatsApp);
    }

    // ── TextBlock ─────────────────────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThrough_WhenTextIsWithinLimit()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello!")]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().Be("Hello!");
    }

    [Fact]
    public void Transform_ShouldTruncateText_WhenTextExceeds4096Characters()
    {
        var longText = new string('A', 5000);
        var envelope = new MessageEnvelope([new TextBlock(longText)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().HaveLength(4096);
    }

    // ── ImageBlock ────────────────────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThroughImageBlock()
    {
        var image = new ImageBlock("https://example.com/img.jpg", "A caption", "image/jpeg");
        var envelope = new MessageEnvelope([image]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<ImageBlock>().Subject;
        block.Url.Should().Be("https://example.com/img.jpg");
        block.Caption.Should().Be("A caption");
    }

    // ── InteractiveBlock ──────────────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThroughInteractive_WhenWithinLimits()
    {
        var interactive = new InteractiveBlock("Choose one:", [
            new QuickReply("1", "Option A"),
            new QuickReply("2", "Option B"),
        ]);
        var envelope = new MessageEnvelope([interactive]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies.Should().HaveCount(2);
        block.Replies[0].Title.Should().Be("Option A");
    }

    [Fact]
    public void Transform_ShouldCapButtonsAtThree_WhenMoreThanThreeProvided()
    {
        var interactive = new InteractiveBlock("Choose:", [
            new QuickReply("1", "A"),
            new QuickReply("2", "B"),
            new QuickReply("3", "C"),
            new QuickReply("4", "D"),
            new QuickReply("5", "E"),
        ]);
        var envelope = new MessageEnvelope([interactive]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies.Should().HaveCount(3);
    }

    [Fact]
    public void Transform_ShouldTruncateButtonTitle_WhenExceeds20Characters()
    {
        var interactive = new InteractiveBlock("Pick:", [
            new QuickReply("1", "This title is way too long for WhatsApp"),
        ]);
        var envelope = new MessageEnvelope([interactive]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies[0].Title.Should().HaveLength(20);
    }

    // ── Empty envelope ────────────────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldReturnSameEnvelope_WhenEmpty()
    {
        var envelope = new MessageEnvelope([]);

        var result = Transformer.Transform(envelope);

        result.Blocks.Should().BeEmpty();
    }
}
