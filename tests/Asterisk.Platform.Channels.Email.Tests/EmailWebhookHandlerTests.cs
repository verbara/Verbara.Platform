using System.Text;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Email;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Channels.Email.Tests;

public class EmailWebhookHandlerTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static EmailWebhookHandler CreateSut(IMessageStore? store = null)
    {
        var messageStore = store ?? Substitute.For<IMessageStore>();
        messageStore.FindByExternalIdAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var parser = new SimpleEmailParser();
        var resolver = new EmailThreadResolver(messageStore, NullLogger<EmailThreadResolver>.Instance);
        return new EmailWebhookHandler(parser, resolver, NullLogger<EmailWebhookHandler>.Instance);
    }

    private static ReadOnlyMemory<byte> ToBytes(string raw) =>
        new(Encoding.UTF8.GetBytes(raw));

    [Fact]
    public void Channel_ShouldBeEmail()
    {
        CreateSut().Channel.Should().Be(ChannelType.Email);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_ForValidPlainEmail()
    {
        var raw = """
            Message-ID: <test@example.com>
            From: alice@example.com
            Subject: Hello

            Hello there!
            """;

        var result = await CreateSut().HandleAsync(
            ToBytes(raw),
            new Dictionary<string, string>(),
            Tenant,
            CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        result.Message.Should().NotBeNull();
        result.Message!.From.Address.Should().Be("alice@example.com");
        result.Message.ExternalMessageId.Should().Be("<test@example.com>");
    }

    [Fact]
    public async Task HandleAsync_ShouldExtractTextBody_AsTextBlock()
    {
        var raw = """
            Message-ID: <id@example.com>
            From: bob@example.com
            Subject: Test

            This is the body text.
            """;

        var result = await CreateSut().HandleAsync(
            ToBytes(raw),
            new Dictionary<string, string>(),
            Tenant,
            CancellationToken.None);

        result.Message!.Content.Blocks.Should().HaveCount(1);
        result.Message.Content.Blocks[0].Should().BeOfType<TextBlock>()
            .Which.Text.Should().Contain("This is the body text.");
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenEmailHasNoFrom()
    {
        var raw = """
            Message-ID: <nofrom@example.com>
            Subject: Test

            Body
            """;

        var result = await CreateSut().HandleAsync(
            ToBytes(raw),
            new Dictionary<string, string>(),
            Tenant,
            CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldIncludeAttachmentAsFileBlock_WhenEmailHasAttachment()
    {
        var raw = """
            Message-ID: <att@example.com>
            From: alice@example.com
            Subject: With Attachment
            Content-Type: multipart/mixed; boundary="b1"

            --b1
            Content-Type: text/plain

            See attached file.
            --b1
            Content-Type: application/pdf
            Content-Disposition: attachment; filename="report.pdf"

            JVBERi0xLjQ=
            --b1--
            """;

        var result = await CreateSut().HandleAsync(
            ToBytes(raw),
            new Dictionary<string, string>(),
            Tenant,
            CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        var blocks = result.Message!.Content.Blocks;
        blocks.Should().Contain(b => b is FileBlock);
        var file = blocks.OfType<FileBlock>().First();
        file.FileName.Should().Be("report.pdf");
    }
}
