using System.Text.Json;
using Asterisk.Platform.Conversations.Serialization;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations.Tests;

public class MessageBlockTests
{
    [Fact]
    public void TextBlock_ShouldRoundTripJson()
    {
        var block = new TextBlock("Hello, world!");

        var json = JsonSerializer.Serialize(block, ConversationsJsonContext.Default.TextBlock);
        var result = JsonSerializer.Deserialize(json, ConversationsJsonContext.Default.TextBlock);

        result.Should().NotBeNull();
        result!.Text.Should().Be("Hello, world!");
        result.Type.Should().Be(MessageBlockType.Text);
    }

    [Fact]
    public void ImageBlock_ShouldRoundTripJson()
    {
        var block = new ImageBlock("https://example.com/img.jpg", "A photo", "image/jpeg");

        var json = JsonSerializer.Serialize(block, ConversationsJsonContext.Default.ImageBlock);
        var result = JsonSerializer.Deserialize(json, ConversationsJsonContext.Default.ImageBlock);

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://example.com/img.jpg");
        result.Caption.Should().Be("A photo");
        result.MimeType.Should().Be("image/jpeg");
    }

    [Fact]
    public void AudioBlock_ShouldRoundTripJson()
    {
        var block = new AudioBlock("https://example.com/audio.ogg", TimeSpan.FromSeconds(30), "audio/ogg");

        var json = JsonSerializer.Serialize(block, ConversationsJsonContext.Default.AudioBlock);
        var result = JsonSerializer.Deserialize(json, ConversationsJsonContext.Default.AudioBlock);

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://example.com/audio.ogg");
        result.Duration.Should().Be(TimeSpan.FromSeconds(30));
        result.MimeType.Should().Be("audio/ogg");
    }

    [Fact]
    public void VideoBlock_ShouldRoundTripJson()
    {
        var block = new VideoBlock("https://example.com/video.mp4", "A clip", "video/mp4");

        var json = JsonSerializer.Serialize(block, ConversationsJsonContext.Default.VideoBlock);
        var result = JsonSerializer.Deserialize(json, ConversationsJsonContext.Default.VideoBlock);

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://example.com/video.mp4");
        result.Caption.Should().Be("A clip");
        result.MimeType.Should().Be("video/mp4");
    }

    [Fact]
    public void FileBlock_ShouldRoundTripJson()
    {
        var block = new FileBlock("https://example.com/doc.pdf", "document.pdf", "application/pdf", 204800L);

        var json = JsonSerializer.Serialize(block, ConversationsJsonContext.Default.FileBlock);
        var result = JsonSerializer.Deserialize(json, ConversationsJsonContext.Default.FileBlock);

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://example.com/doc.pdf");
        result.FileName.Should().Be("document.pdf");
        result.MimeType.Should().Be("application/pdf");
        result.SizeBytes.Should().Be(204800L);
    }

    [Fact]
    public void LocationBlock_ShouldRoundTripJson()
    {
        var block = new LocationBlock(37.7749, -122.4194, "San Francisco");

        var json = JsonSerializer.Serialize(block, ConversationsJsonContext.Default.LocationBlock);
        var result = JsonSerializer.Deserialize(json, ConversationsJsonContext.Default.LocationBlock);

        result.Should().NotBeNull();
        result!.Latitude.Should().Be(37.7749);
        result.Longitude.Should().Be(-122.4194);
        result.Name.Should().Be("San Francisco");
    }

    [Fact]
    public void InteractiveBlock_ShouldRoundTripJson()
    {
        var replies = new List<QuickReply>
        {
            new("yes", "Yes"),
            new("no", "No"),
        };
        var block = new InteractiveBlock("Do you confirm?", replies);

        var json = JsonSerializer.Serialize(block, ConversationsJsonContext.Default.InteractiveBlock);
        var result = JsonSerializer.Deserialize(json, ConversationsJsonContext.Default.InteractiveBlock);

        result.Should().NotBeNull();
        result!.Body.Should().Be("Do you confirm?");
        result.Replies.Should().HaveCount(2);
        result.Replies[0].Id.Should().Be("yes");
        result.Replies[1].Title.Should().Be("No");
    }

    [Fact]
    public void MessageEnvelope_ShouldRoundTripJson()
    {
        var envelope = new MessageEnvelope(new List<MessageBlock>
        {
            new TextBlock("Hello!"),
            new ImageBlock("https://example.com/img.png", null, "image/png"),
        });

        var json = JsonSerializer.Serialize(envelope, ConversationsJsonContext.Default.MessageEnvelope);
        var result = JsonSerializer.Deserialize(json, ConversationsJsonContext.Default.MessageEnvelope);

        result.Should().NotBeNull();
        result!.Blocks.Should().HaveCount(2);
        result.Blocks[0].Should().BeOfType<TextBlock>();
        result.Blocks[1].Should().BeOfType<ImageBlock>();
    }

    [Fact]
    public void Message_ShouldCreateWithRequiredProperties()
    {
        var message = new Message
        {
            MessageId = EntityId.From("msg-001"),
            ConversationId = EntityId.From("conv-001"),
            TenantId = new TenantId("t1"),
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope(new List<MessageBlock> { new TextBlock("Hi") }),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        message.MessageId.Value.Should().Be("msg-001");
        message.Direction.Should().Be(MessageDirection.Inbound);
        message.Channel.Should().Be(ChannelType.WhatsApp);
        message.DeliveryStatus.Should().Be(MessageDeliveryStatus.Delivered);
        message.SenderId.Should().BeNull();
        message.ExternalMessageId.Should().BeNull();
    }

    [Fact]
    public void Conversation_ShouldExposeChannel_WhenCreated()
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.From("conv-001"),
            TenantId = new TenantId("t1"),
            ContactId = EntityId.From("c-001"),
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        conv.Channel.Should().Be(ChannelType.WhatsApp);
    }
}
