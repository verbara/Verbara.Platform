using Asterisk.Platform.Channels.Telegram;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Telegram.Tests;

public class TelegramMessageTransformerTests
{
    private static readonly TelegramMessageTransformer Transformer = new();

    // ── TextBlock → text message ──────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThroughTextBlock_WhenWithinLimit()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello!")]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().Be("Hello!");
    }

    [Fact]
    public void Transform_ShouldTruncateText_WhenExceeds4096Characters()
    {
        var longText = new string('x', 5000);
        var envelope = new MessageEnvelope([new TextBlock(longText)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().HaveLength(4096);
    }

    // ── ImageBlock → Telegram photo ───────────────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThroughImageBlock_WhenCaptionWithinLimit()
    {
        var envelope = new MessageEnvelope([new ImageBlock("https://example.com/img.jpg", "Caption", "image/jpeg")]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<ImageBlock>().Subject;
        block.Url.Should().Be("https://example.com/img.jpg");
        block.Caption.Should().Be("Caption");
    }

    [Fact]
    public void Transform_ShouldTruncateImageCaption_WhenExceeds1024Characters()
    {
        var longCaption = new string('c', 2000);
        var envelope = new MessageEnvelope([new ImageBlock("https://example.com/img.jpg", longCaption, null)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<ImageBlock>().Subject;
        block.Caption!.Should().HaveLength(1024);
    }

    // ── LocationBlock → Telegram location ────────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThroughLocationBlock()
    {
        var envelope = new MessageEnvelope([new LocationBlock(51.5074, -0.1278, "London")]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<LocationBlock>().Subject;
        block.Latitude.Should().Be(51.5074);
        block.Longitude.Should().Be(-0.1278);
    }

    // ── InteractiveBlock → inline keyboard ───────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThroughInteractiveBlock_WhenButtonsWithinLimit()
    {
        var replies = new List<QuickReply>
        {
            new("btn1", "Option 1"),
            new("btn2", "Option 2"),
        };
        var envelope = new MessageEnvelope([new InteractiveBlock("Choose:", replies)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Body.Should().Be("Choose:");
        block.Replies.Should().HaveCount(2);
    }

    [Fact]
    public void Transform_ShouldTruncateButtonText_WhenExceeds64Characters()
    {
        var longTitle = new string('b', 100);
        var replies = new List<QuickReply> { new("btn1", longTitle) };
        var envelope = new MessageEnvelope([new InteractiveBlock("Pick one:", replies)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies[0].Title.Should().HaveLength(64);
    }

    // ── TargetChannel ─────────────────────────────────────────────────────────

    [Fact]
    public void TargetChannel_ShouldBeTelegram()
    {
        Transformer.TargetChannel.Should().Be(ChannelType.Telegram);
    }
}
