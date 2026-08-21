using System.Text;
using Verbara.Platform.Channels.Email;

namespace Verbara.Platform.Channels.Email.Tests;

public class SimpleEmailParserTests
{
    private readonly SimpleEmailParser _sut = new();

    private static ReadOnlyMemory<byte> ToBytes(string raw) =>
        new(Encoding.UTF8.GetBytes(raw));

    [Fact]
    public void Parse_ShouldExtractMessageId_FromHeader()
    {
        var raw = """
            Message-ID: <abc123@mail.example.com>
            From: alice@example.com
            Subject: Test
            Date: Mon, 01 Jan 2024 10:00:00 +0000

            Hello world
            """;

        var result = _sut.Parse(ToBytes(raw));

        result.MessageId.Should().Be("<abc123@mail.example.com>");
    }

    [Fact]
    public void Parse_ShouldExtractInReplyTo_AndReferences()
    {
        var raw = """
            Message-ID: <new@mail.example.com>
            In-Reply-To: <prev@mail.example.com>
            References: <orig@mail.example.com> <prev@mail.example.com>
            From: alice@example.com
            Subject: Re: Test

            Reply body
            """;

        var result = _sut.Parse(ToBytes(raw));

        result.InReplyTo.Should().Be("<prev@mail.example.com>");
        result.References.Should().Be("<orig@mail.example.com> <prev@mail.example.com>");
    }

    [Fact]
    public void Parse_ShouldExtractTextBody_FromPlainEmail()
    {
        var raw = """
            Message-ID: <id@example.com>
            From: alice@example.com
            Subject: Hello
            Content-Type: text/plain

            This is the email body.
            """;

        var result = _sut.Parse(ToBytes(raw));

        result.TextBody.Should().Contain("This is the email body.");
        result.HtmlBody.Should().BeNull();
    }

    [Fact]
    public void Parse_ShouldExtractFrom_FromHeader()
    {
        var raw = """
            From: Bob Smith <bob@example.com>
            Subject: Greetings

            Hi
            """;

        var result = _sut.Parse(ToBytes(raw));

        result.From.Should().Be("Bob Smith <bob@example.com>");
    }

    [Fact]
    public void Parse_ShouldExtractSubject()
    {
        var raw = """
            Subject: My Important Email
            From: x@y.com

            Body
            """;

        var result = _sut.Parse(ToBytes(raw));

        result.Subject.Should().Be("My Important Email");
    }

    [Fact]
    public void Parse_ShouldGenerateMessageId_WhenHeaderMissing()
    {
        var raw = """
            From: alice@example.com
            Subject: No ID

            Body
            """;

        var result = _sut.Parse(ToBytes(raw));

        result.MessageId.Should().StartWith("<").And.EndWith(">");
    }

    [Fact]
    public void Parse_ShouldHandleMultipartAlternative_ExtractingTextAndHtml()
    {
        var raw = """
            Message-ID: <multi@example.com>
            From: alice@example.com
            Subject: Multipart
            Content-Type: multipart/alternative; boundary="boundary42"

            --boundary42
            Content-Type: text/plain

            Plain text body
            --boundary42
            Content-Type: text/html

            <html><body><p>HTML body</p></body></html>
            --boundary42--
            """;

        var result = _sut.Parse(ToBytes(raw));

        result.TextBody.Should().Contain("Plain text body");
        result.HtmlBody.Should().Contain("<p>HTML body</p>");
    }

    [Fact]
    public void Parse_ShouldExtractAttachments_FromMultipartMixed()
    {
        var raw = """
            Message-ID: <att@example.com>
            From: alice@example.com
            Subject: With Attachment
            Content-Type: multipart/mixed; boundary="bound1"

            --bound1
            Content-Type: text/plain

            See attached.
            --bound1
            Content-Type: application/pdf
            Content-Disposition: attachment; filename="report.pdf"

            JVBERi0xLjQ=
            --bound1--
            """;

        var result = _sut.Parse(ToBytes(raw));

        result.Attachments.Should().HaveCount(1);
        result.Attachments[0].FileName.Should().Be("report.pdf");
        result.Attachments[0].ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public void Parse_ShouldNormaliseReceivedAtToUtc_WhenDateHeaderCarriesNonZeroOffset()
    {
        var raw = """
            Message-ID: <tz@example.com>
            From: alice@example.com
            Subject: Offset header
            Date: Mon, 01 Jan 2024 10:00:00 -0500

            Body
            """;

        var result = _sut.Parse(ToBytes(raw));

        result.ReceivedAt.Offset.Should().Be(TimeSpan.Zero);
        result.ReceivedAt.UtcTicks.Should()
            .Be(new DateTimeOffset(2024, 1, 1, 15, 0, 0, TimeSpan.Zero).UtcTicks);
    }
}
