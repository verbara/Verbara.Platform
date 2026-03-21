using Asterisk.Platform.Channels.Email;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Email.Tests;

public class EmailMessageTransformerTests
{
    private readonly EmailMessageTransformer _sut = new();

    [Fact]
    public void TargetChannel_ShouldBeEmail()
    {
        _sut.TargetChannel.Should().Be(ChannelType.Email);
    }

    [Fact]
    public void Transform_ShouldReturnSameEnvelope_WhenEmpty()
    {
        var envelope = new MessageEnvelope([]);

        var result = _sut.Transform(envelope);

        result.Should().BeSameAs(envelope);
    }

    [Fact]
    public void Transform_ShouldKeepTextBlock_Unchanged()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello, world!")]);

        var result = _sut.Transform(envelope);

        result.Blocks.Should().HaveCount(1);
        result.Blocks[0].Should().BeOfType<TextBlock>()
            .Which.Text.Should().Be("Hello, world!");
    }

    [Fact]
    public void Transform_ShouldKeepImageBlock_Unchanged()
    {
        var envelope = new MessageEnvelope([new ImageBlock("https://cdn.example.com/img.png", "Caption", "image/png")]);

        var result = _sut.Transform(envelope);

        result.Blocks[0].Should().BeOfType<ImageBlock>();
    }

    [Fact]
    public void Transform_ShouldKeepFileBlock_Unchanged()
    {
        var envelope = new MessageEnvelope([new FileBlock("https://cdn.example.com/file.pdf", "file.pdf", "application/pdf", 1024)]);

        var result = _sut.Transform(envelope);

        result.Blocks[0].Should().BeOfType<FileBlock>();
    }

    [Fact]
    public void Transform_ShouldFlattenInteractiveBlock_ToTextBlock()
    {
        var interactive = new InteractiveBlock(
            "Choose an option:",
            [new QuickReply("opt1", "Option A"), new QuickReply("opt2", "Option B")]);
        var envelope = new MessageEnvelope([interactive]);

        var result = _sut.Transform(envelope);

        result.Blocks[0].Should().BeOfType<TextBlock>();
        var text = (TextBlock)result.Blocks[0];
        text.Text.Should().Contain("Choose an option:");
        text.Text.Should().Contain("Option A");
        text.Text.Should().Contain("Option B");
    }

    [Fact]
    public void Transform_ShouldFlattenLocationBlock_ToTextBlock()
    {
        var location = new LocationBlock(48.8566, 2.3522, "Paris");
        var envelope = new MessageEnvelope([location]);

        var result = _sut.Transform(envelope);

        result.Blocks[0].Should().BeOfType<TextBlock>();
        var text = (TextBlock)result.Blocks[0];
        text.Text.Should().Contain("Paris");
        text.Text.Should().Contain("48.8566");
        text.Text.Should().Contain("2.3522");
    }

    [Fact]
    public void Transform_ShouldConvertAudioBlock_ToFileBlock()
    {
        var audio = new AudioBlock("https://cdn.example.com/sound.mp3", null, "audio/mpeg");
        var envelope = new MessageEnvelope([audio]);

        var result = _sut.Transform(envelope);

        result.Blocks[0].Should().BeOfType<FileBlock>()
            .Which.FileName.Should().Be("sound.mp3");
    }

    [Fact]
    public void Transform_ShouldConvertVideoBlock_ToFileBlock()
    {
        var video = new VideoBlock("https://cdn.example.com/clip.mp4", "Watch this", "video/mp4");
        var envelope = new MessageEnvelope([video]);

        var result = _sut.Transform(envelope);

        result.Blocks[0].Should().BeOfType<FileBlock>()
            .Which.FileName.Should().Be("clip.mp4");
    }
}
