using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Twitter;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Asterisk.Platform.Channels.Twitter.Tests;

public class TwitterMessageTransformerTests
{
    private static readonly TwitterMessageTransformer Transformer = new();

    // ── TextBlock → DM text ───────────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThroughTextBlock_WhenWithinLimit()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello from X!")]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().Be("Hello from X!");
    }

    [Fact]
    public void Transform_ShouldTruncateText_WhenExceeds10000Characters()
    {
        var longText = new string('x', 12_000);
        var envelope = new MessageEnvelope([new TextBlock(longText)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().HaveLength(10_000);
    }

    // ── ImageBlock → text fallback ────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldConvertImageBlockToText_WithCaption()
    {
        var envelope = new MessageEnvelope([new ImageBlock("https://example.com/img.jpg", "A nice photo", "image/jpeg")]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().Contain("A nice photo");
        block.Text.Should().Contain("https://example.com/img.jpg");
    }

    [Fact]
    public void Transform_ShouldConvertImageBlockToText_WithoutCaption()
    {
        var envelope = new MessageEnvelope([new ImageBlock("https://example.com/img.jpg", null, "image/jpeg")]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().Contain("[Image]");
        block.Text.Should().Contain("https://example.com/img.jpg");
    }

    // ── FileBlock → text fallback ─────────────────────────────────────────────

    [Fact]
    public void Transform_ShouldConvertFileBlockToText()
    {
        var envelope = new MessageEnvelope([new FileBlock("https://example.com/doc.pdf", "report.pdf", "application/pdf", 1024)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        block.Text.Should().Contain("report.pdf");
        block.Text.Should().Contain("https://example.com/doc.pdf");
    }

    // ── InteractiveBlock → quick replies ──────────────────────────────────────

    [Fact]
    public void Transform_ShouldPassThroughInteractiveBlock_WhenRepliesWithinLimits()
    {
        var replies = new List<QuickReply>
        {
            new("opt1", "Option 1"),
            new("opt2", "Option 2"),
        };
        var envelope = new MessageEnvelope([new InteractiveBlock("Choose:", replies)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Body.Should().Be("Choose:");
        block.Replies.Should().HaveCount(2);
    }

    [Fact]
    public void Transform_ShouldTruncateOptionLabel_WhenExceeds36Characters()
    {
        var longTitle = new string('L', 60);
        var replies = new List<QuickReply> { new("opt1", longTitle) };
        var envelope = new MessageEnvelope([new InteractiveBlock("Pick:", replies)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies[0].Title.Should().HaveLength(36);
    }

    [Fact]
    public void Transform_ShouldTruncateTo20Options_WhenMoreThan20RepliesProvided()
    {
        var replies = Enumerable.Range(1, 25)
            .Select(i => new QuickReply($"id{i}", $"Option {i}"))
            .ToList();
        var envelope = new MessageEnvelope([new InteractiveBlock("Choose:", replies)]);

        var result = Transformer.Transform(envelope);

        var block = result.Blocks.Should().ContainSingle().Which.Should().BeOfType<InteractiveBlock>().Subject;
        block.Replies.Should().HaveCount(20);
    }

    // ── TargetChannel ─────────────────────────────────────────────────────────

    [Fact]
    public void TargetChannel_ShouldBeTwitter()
    {
        Transformer.TargetChannel.Should().Be(ChannelType.Twitter);
    }

    // ── DI registration ───────────────────────────────────────────────────────

    [Fact]
    public void AddTwitter_ShouldRegisterServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ITenantChannelConfigStore>());

        services.AddTwitter(o =>
        {
            o.ApiKey = "key";
            o.ApiSecret = "secret";
            o.AccessToken = "token";
            o.AccessTokenSecret = "token-secret";
            o.BearerToken = "bearer";
        });

        var provider = services.BuildServiceProvider();

        provider.GetService<TwitterWebhookHandler>().Should().NotBeNull();
        provider.GetService<TwitterMessageTransformer>().Should().NotBeNull();
    }
}
