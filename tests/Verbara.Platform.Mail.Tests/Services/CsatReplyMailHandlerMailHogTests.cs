using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Verbara.Platform.Mail.Services;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// End-to-end CSAT email reply capture over a real MailHog SMTP hop (csat-runner Phase C, 3.3).
/// Sends a signed-token reply through MailHog's SMTP, reads the stored raw MIME back via MailHog's
/// HTTP API, parses it with MailKit, and runs it through <see cref="CsatReplyMailHandler"/> — proving
/// the parse + token-verify + rating-extraction path works on a message that made a genuine SMTP
/// round-trip, not a hand-built object.
/// </summary>
[Collection("MailHog")]
public sealed class CsatReplyMailHandlerMailHogTests
{
    private readonly MailHogFixture _fixture;
    private static readonly DateTimeOffset Now = new(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);
    private readonly HmacCsatReplyTokenSigner _signer = new("test-signing-secret-0123456789abcdef", TimeSpan.FromDays(7));

    public CsatReplyMailHandlerMailHogTests(MailHogFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task HandleReplyAsync_ShouldCaptureRating_WhenReplyMadeRealSmtpRoundTripThroughMailHog()
    {
        var token = _signer.Sign("ten-42", "conv-1", "resp-1", Now);

        // 1) Build + send a CSAT reply through MailHog's SMTP.
        var sent = new MimeMessage();
        sent.From.Add(new MailboxAddress("Visitor", "visitor@example.com"));
        sent.To.Add(new MailboxAddress("CSAT", $"csat+{token}@tenant.verbara.io"));
        sent.ReplyTo.Add(new MailboxAddress("CSAT", $"csat+{token}@tenant.verbara.io"));
        sent.Subject = "5";
        sent.Body = new TextPart("plain") { Text = "Great support!" };

        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync(_fixture.SmtpHost, _fixture.SmtpPort, SecureSocketOptions.None);
            await smtp.SendAsync(sent);
            await smtp.DisconnectAsync(quit: true);
        }

        // 2) Read the stored raw MIME back via MailHog's HTTP API and parse it.
        var received = await FetchLatestMimeAsync();
        received.Should().NotBeNull();

        // 3) Run the round-tripped message through the handler.
        var forwarder = new RecordingForwarder();
        var handler = new CsatReplyMailHandler(
            _signer,
            new ByTokenResolver(),
            forwarder,
            timeProvider: new FakeTimeProvider(Now));

        var outcome = await handler.HandleReplyAsync(received!, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.Captured);
        forwarder.Calls.Should().ContainSingle().Which.Rating.Should().Be(5);
    }

    private async Task<MimeMessage?> FetchLatestMimeAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{_fixture.SmtpHost}:{_fixture.HttpPort}") };

        // MailHog delivers asynchronously; poll the API briefly for the message to land.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var json = await http.GetStringAsync("/api/v1/messages");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var raw = doc.RootElement[0].GetProperty("Raw");
                var data = raw.GetProperty("Data").GetString();
                if (!string.IsNullOrEmpty(data))
                {
                    var bytes = Encoding.UTF8.GetBytes(data);
                    using var stream = new MemoryStream(bytes);
                    return await MimeMessage.LoadAsync(stream);
                }
            }

            await Task.Delay(250);
        }

        return null;
    }

    private sealed class ByTokenResolver : ICsatEmailDispatchResolver
    {
        private static readonly CsatEmailDispatch Dispatch = new("ten-42", "srv-csat-v1", "support-tier1", "conv-1");
        public Task<CsatEmailDispatch?> ResolveByTokenAsync(string token, CancellationToken ct) => Task.FromResult<CsatEmailDispatch?>(Dispatch);
        public Task<CsatEmailDispatch?> ResolveByInReplyToAsync(string inReplyToMessageId, CancellationToken ct) => Task.FromResult<CsatEmailDispatch?>(null);
    }

    private sealed class RecordingForwarder : ICsatEmailCaptureForwarder
    {
        public List<(CsatEmailDispatch Dispatch, int Rating, DateTimeOffset CapturedAt)> Calls { get; } = [];
        public Task ForwardEmailRatingAsync(CsatEmailDispatch dispatch, int rating, DateTimeOffset capturedAt, CancellationToken ct)
        {
            Calls.Add((dispatch, rating, capturedAt));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
