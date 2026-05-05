using Verbara.Platform.Channels.Rcs;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.Rcs.Tests;

public class RcsBuildMessageTests
{
    [Fact]
    public void BuildRcsMessage_ShouldReturnNull_WhenEnvelopeIsEmpty()
    {
        var result = RcsConnector.BuildRcsMessage(new MessageEnvelope([]));

        result.Should().BeNull();
    }

    [Fact]
    public void BuildRcsMessage_ShouldReturnRcsTextMessage_WhenSingleTextBlock()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello RCS")]);

        var result = RcsConnector.BuildRcsMessage(envelope);

        result.Should().BeOfType<RcsTextMessage>()
            .Which.Text.Should().Be("Hello RCS");
    }

    [Fact]
    public void BuildRcsMessage_ShouldReturnRcsRichCardMessage_WhenSingleImageBlock()
    {
        var envelope = new MessageEnvelope([new ImageBlock("https://example.com/img.jpg", "My Image", null)]);

        var result = RcsConnector.BuildRcsMessage(envelope);

        var card = result.Should().BeOfType<RcsRichCardMessage>().Subject;
        card.ImageUrl.Should().Be("https://example.com/img.jpg");
        card.Title.Should().Be("My Image");
    }

    [Fact]
    public void BuildRcsMessage_ShouldReturnRcsRichCardWithReplySuggestions_WhenInteractiveBlock()
    {
        var replies = new List<QuickReply>
        {
            new("yes", "Yes"),
            new("no", "No"),
        };
        var envelope = new MessageEnvelope([new InteractiveBlock("Do you agree?", replies)]);

        var result = RcsConnector.BuildRcsMessage(envelope);

        var card = result.Should().BeOfType<RcsRichCardMessage>().Subject;
        card.Title.Should().Be("Do you agree?");
        card.Suggestions.Should().HaveCount(2);
        card.Suggestions[0].Should().BeOfType<RcsReplySuggestion>()
            .Which.Text.Should().Be("Yes");
        card.Suggestions[1].Should().BeOfType<RcsReplySuggestion>()
            .Which.Text.Should().Be("No");
    }

    [Fact]
    public void BuildRcsMessage_ShouldReturnRcsCarouselMessage_WhenMultipleImageBlocks()
    {
        var envelope = new MessageEnvelope(
        [
            new ImageBlock("https://example.com/img1.jpg", "Card 1", null),
            new ImageBlock("https://example.com/img2.jpg", "Card 2", null),
        ]);

        var result = RcsConnector.BuildRcsMessage(envelope);

        var carousel = result.Should().BeOfType<RcsCarouselMessage>().Subject;
        carousel.Cards.Should().HaveCount(2);
        carousel.Cards[0].ImageUrl.Should().Be("https://example.com/img1.jpg");
        carousel.Cards[1].ImageUrl.Should().Be("https://example.com/img2.jpg");
    }

    [Fact]
    public void BuildRcsMessage_ShouldReturnCarousel_WhenMixedImageAndInteractiveBlocks()
    {
        var envelope = new MessageEnvelope(
        [
            new ImageBlock("https://example.com/promo.jpg", "Special Offer", null),
            new InteractiveBlock("Interested?", [new QuickReply("learn-more", "Learn More")]),
        ]);

        var result = RcsConnector.BuildRcsMessage(envelope);

        var carousel = result.Should().BeOfType<RcsCarouselMessage>().Subject;
        carousel.Cards.Should().HaveCount(2);
    }
}
