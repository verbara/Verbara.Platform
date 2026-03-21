using Asterisk.Platform.Channels.Email;

namespace Asterisk.Platform.Channels.Email.Tests;

public class ParsedEmailTests
{
    [Fact]
    public void ParsedEmail_ShouldCreate_WithAllProperties()
    {
        var attachments = new List<EmailAttachment>
        {
            new("report.pdf", "application/pdf", 1024, null),
        };

        var email = new ParsedEmail(
            MessageId: "<abc@mail.example.com>",
            InReplyTo: "<prev@mail.example.com>",
            References: "<prev@mail.example.com>",
            From: "alice@example.com",
            Subject: "Hello",
            TextBody: "Body text",
            HtmlBody: "<p>Body text</p>",
            Attachments: attachments,
            ReceivedAt: DateTimeOffset.UtcNow);

        email.MessageId.Should().Be("<abc@mail.example.com>");
        email.InReplyTo.Should().Be("<prev@mail.example.com>");
        email.References.Should().Be("<prev@mail.example.com>");
        email.From.Should().Be("alice@example.com");
        email.Subject.Should().Be("Hello");
        email.TextBody.Should().Be("Body text");
        email.HtmlBody.Should().Be("<p>Body text</p>");
        email.Attachments.Should().HaveCount(1);
    }

    [Fact]
    public void EmailAttachment_ShouldCreate_WithContentId()
    {
        var att = new EmailAttachment("logo.png", "image/png", 2048, "<logo@cid>");

        att.FileName.Should().Be("logo.png");
        att.ContentType.Should().Be("image/png");
        att.SizeBytes.Should().Be(2048);
        att.ContentId.Should().Be("<logo@cid>");
    }

    [Fact]
    public void EmailAttachment_ShouldCreate_WithoutContentId()
    {
        var att = new EmailAttachment("doc.pdf", "application/pdf", 512, null);

        att.ContentId.Should().BeNull();
    }
}
