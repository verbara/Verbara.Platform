using System.Net.Mail;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Email;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Channels.Email.Tests;

public class EmailConnectorTests
{
    private static EmailOptions DefaultOptions() => new()
    {
        SmtpHost = "smtp.example.com",
        SmtpPort = 587,
        SmtpUsername = "user@example.com",
        SmtpPassword = "secret",
        UseSsl = true,
        DefaultFromAddress = "noreply@example.com",
        DefaultFromName = "Support",
        MaxAttachmentSizeMb = 25,
    };

    private static (EmailConnector connector, ISmtpClientWrapper smtp, IEmailThreadingContext threading) CreateConnector(
        EmailOptions? options = null)
    {
        var smtp = Substitute.For<ISmtpClientWrapper>();
        smtp.SendAsync(Arg.Any<MailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var threading = Substitute.For<IEmailThreadingContext>();
        threading.GetThreadInfo(Arg.Any<EntityId>()).Returns((EmailThreadInfo?)null);

        var connector = new EmailConnector(
            smtp,
            Options.Create(options ?? DefaultOptions()),
            threading,
            NullLogger<EmailConnector>.Instance);

        return (connector, smtp, threading);
    }

    private static OutboundMessage TextMessage(string to, string text, string subject = "Test") =>
        new(
            new ChannelAddress(ChannelType.Email, to),
            new MessageEnvelope([new TextBlock(text)]),
            new TenantId("tenant-1"),
            EntityId.From("conv-1"),
            subject);

    [Fact]
    public void Channel_ShouldBeEmail()
    {
        var (connector, _, _) = CreateConnector();

        connector.Channel.Should().Be(ChannelType.Email);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnSuccess_WhenSmtpSucceeds()
    {
        var (connector, smtp, _) = CreateConnector();

        var result = await connector.SendAsync(
            TextMessage("alice@example.com", "Hello!"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().NotBeNullOrEmpty();
        result.ErrorCode.Should().BeNull();
        await smtp.Received(1).SendAsync(Arg.Any<MailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenSmtpThrows()
    {
        var (connector, smtp, _) = CreateConnector();
        smtp.SendAsync(Arg.Any<MailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new SmtpException("Connection refused")));

        var result = await connector.SendAsync(
            TextMessage("alice@example.com", "Hello!"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("SMTP_ERROR");
        result.ErrorMessage.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task SendAsync_ShouldUseTemplateId_AsSubject()
    {
        var (connector, smtp, _) = CreateConnector();

        MailMessage? captured = null;
        await smtp.SendAsync(
            Arg.Do<MailMessage>(m => captured = m),
            Arg.Any<CancellationToken>());

        await connector.SendAsync(
            TextMessage("alice@example.com", "Body", subject: "My Email Subject"),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Subject.Should().Be("My Email Subject");
    }

    [Fact]
    public async Task SendAsync_ShouldDefaultSubjectToMessage_WhenTemplateIdIsNull()
    {
        var (connector, smtp, _) = CreateConnector();

        MailMessage? captured = null;
        await smtp.SendAsync(
            Arg.Do<MailMessage>(m => captured = m),
            Arg.Any<CancellationToken>());

        var msg = new OutboundMessage(
            new ChannelAddress(ChannelType.Email, "alice@example.com"),
            new MessageEnvelope([new TextBlock("Hello")]),
            new TenantId("t1"),
            EntityId.From("c1"));

        await connector.SendAsync(msg, CancellationToken.None);

        captured!.Subject.Should().Be("Message");
    }

    [Fact]
    public async Task SendAsync_ShouldAddInReplyTo_WhenThreadInfoAvailable()
    {
        var (connector, smtp, threading) = CreateConnector();
        threading.GetThreadInfo(Arg.Any<EntityId>())
            .Returns(new EmailThreadInfo("<prev@example.com>", "<orig@example.com> <prev@example.com>"));

        MailMessage? captured = null;
        await smtp.SendAsync(
            Arg.Do<MailMessage>(m => captured = m),
            Arg.Any<CancellationToken>());

        await connector.SendAsync(
            TextMessage("alice@example.com", "Reply body"),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Headers["In-Reply-To"].Should().Be("<prev@example.com>");
        captured.Headers["References"].Should().Contain("<orig@example.com>");
    }

    [Fact]
    public async Task SendAsync_ShouldAddFileAttachment_WhenFileBlockPresent()
    {
        var (connector, smtp, _) = CreateConnector();

        // Capture attachment info inside the callback (before MailMessage is disposed)
        int attachmentCount = 0;
        string? attachmentName = null;
        await smtp.SendAsync(
            Arg.Do<MailMessage>(m =>
            {
                attachmentCount = m.Attachments.Count;
                attachmentName = m.Attachments.Count > 0 ? m.Attachments[0].Name : null;
            }),
            Arg.Any<CancellationToken>());

        var msg = new OutboundMessage(
            new ChannelAddress(ChannelType.Email, "alice@example.com"),
            new MessageEnvelope([
                new TextBlock("See attached."),
                new FileBlock("https://cdn.example.com/doc.pdf", "doc.pdf", "application/pdf", 1024 * 100),
            ]),
            new TenantId("t1"),
            EntityId.From("c1"),
            "Files");

        await connector.SendAsync(msg, CancellationToken.None);

        attachmentCount.Should().Be(1);
        attachmentName.Should().Be("doc.pdf");
    }

    [Fact]
    public async Task SendAsync_ShouldSkipAttachment_WhenFileSizeExceedsLimit()
    {
        var options = DefaultOptions();
        options.MaxAttachmentSizeMb = 1;
        var (connector, smtp, _) = CreateConnector(options);

        // Capture attachment count inside the callback (before MailMessage is disposed)
        int attachmentCount = -1;
        await smtp.SendAsync(
            Arg.Do<MailMessage>(m => attachmentCount = m.Attachments.Count),
            Arg.Any<CancellationToken>());

        var msg = new OutboundMessage(
            new ChannelAddress(ChannelType.Email, "alice@example.com"),
            new MessageEnvelope([
                new FileBlock("https://cdn.example.com/huge.pdf", "huge.pdf", "application/pdf", 5L * 1024 * 1024),
            ]),
            new TenantId("t1"),
            EntityId.From("c1"),
            "Big file");

        await connector.SendAsync(msg, CancellationToken.None);

        attachmentCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStatusAsync_ShouldReturnNull()
    {
        var (connector, _, _) = CreateConnector();

        var result = await connector.GetStatusAsync("msg-id", CancellationToken.None);

        result.Should().BeNull();
    }
}
