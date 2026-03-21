using Asterisk.Platform.Channels.Rcs;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Rcs.Tests;

public class RcsMessageTransformerTests
{
    private static readonly RcsMessageTransformer Transformer = new();

    [Fact]
    public void TargetChannel_ShouldBeRcs()
    {
        Transformer.TargetChannel.Should().Be(ChannelType.Rcs);
    }

    [Fact]
    public void Transform_ShouldReturnSameEnvelope_WhenEmpty()
    {
        var envelope = new MessageEnvelope([]);

        var result = Transformer.Transform(envelope);

        result.Should().BeSameAs(envelope);
    }

    [Fact]
    public void Transform_ShouldReturnSameTextBlock_WhenWithinLimit()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello")]);

        var result = Transformer.Transform(envelope);

        result.Blocks[0].Should().BeOfType<TextBlock>()
            .Which.Text.Should().Be("Hello");
    }

    [Fact]
    public void Transform_ShouldTruncateTextBlock_WhenExceedsMaxLength()
    {
        var longText = new string('X', 3000);
        var envelope = new MessageEnvelope([new TextBlock(longText)]);

        var result = Transformer.Transform(envelope);

        result.Blocks[0].Should().BeOfType<TextBlock>()
            .Which.Text.Should().HaveLength(2048);
    }

    [Fact]
    public void Transform_ShouldTruncateImageCaption_WhenExceedsMaxLength()
    {
        var longCaption = new string('C', 300);
        var envelope = new MessageEnvelope([new ImageBlock("https://example.com/img.jpg", longCaption, null)]);

        var result = Transformer.Transform(envelope);

        result.Blocks[0].Should().BeOfType<ImageBlock>()
            .Which.Caption.Should().HaveLength(200);
    }

    [Fact]
    public void Transform_ShouldTruncateSuggestionLabels_WhenExceedMaxLength()
    {
        var longTitle = new string('S', 50);
        var replies = new List<QuickReply> { new("reply-1", longTitle) };
        var envelope = new MessageEnvelope([new InteractiveBlock("Choose an option", replies)]);

        var result = Transformer.Transform(envelope);

        result.Blocks[0].Should().BeOfType<InteractiveBlock>()
            .Which.Replies[0].Title.Should().HaveLength(25);
    }

    [Fact]
    public void Transform_ShouldTruncateInteractiveBody_WhenExceedsMaxLength()
    {
        var longBody = new string('B', 2500);
        var envelope = new MessageEnvelope([new InteractiveBlock(longBody, [])]);

        var result = Transformer.Transform(envelope);

        result.Blocks[0].Should().BeOfType<InteractiveBlock>()
            .Which.Body.Should().HaveLength(2000);
    }

    [Fact]
    public void Transform_ShouldReturnSameInstance_WhenNothingNeedsTruncation()
    {
        var reply = new QuickReply("id-1", "Short label");
        var envelope = new MessageEnvelope([new InteractiveBlock("Short body", [reply])]);

        var result = Transformer.Transform(envelope);

        // Block instance should be unchanged (no allocation).
        result.Blocks[0].Should().BeSameAs(envelope.Blocks[0]);
    }

    [Fact]
    public void Transform_ShouldPassThroughUnsupportedBlocks_Unchanged()
    {
        var audioBlock = new AudioBlock("https://example.com/audio.mp3", null, null);
        var envelope = new MessageEnvelope([audioBlock]);

        var result = Transformer.Transform(envelope);

        result.Blocks[0].Should().BeSameAs(audioBlock);
    }
}
