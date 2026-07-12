using MimeKit;
using Verbara.Platform.Mail.Services;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Tests.Services;

public sealed class CsatReplyMailHandlerTests
{
    private const string Secret = "test-signing-secret-0123456789abcdef";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly DateTimeOffset Now = new(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);

    private readonly HmacCsatReplyTokenSigner _signer = new(Secret, Ttl);

    // ─── token valid / invalid ───────────────────────────────────────────────────

    [Fact]
    public async Task HandleReplyAsync_ShouldCapture_WhenTokenValidAndSubjectHasRating()
    {
        var token = _signer.Sign("ten-42", "conv-1", "resp-1", Now);
        var resolver = new FakeResolver { ByToken = DispatchFor("ten-42") };
        var forwarder = new RecordingForwarder();
        var sut = NewHandler(resolver, forwarder);

        var message = BuildReply(replyToLocalPart: $"csat+{token}", subject: "5", body: "");
        var outcome = await sut.HandleReplyAsync(message, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.Captured);
        forwarder.Calls.Should().ContainSingle().Which.Rating.Should().Be(5);
    }

    [Fact]
    public async Task HandleReplyAsync_ShouldReturnTokenInvalid_WhenTokenTampered()
    {
        var token = _signer.Sign("ten-42", "conv-1", "resp-1", Now) + "tampered";
        var resolver = new FakeResolver();
        var forwarder = new RecordingForwarder();
        var sut = NewHandler(resolver, forwarder);

        var message = BuildReply(replyToLocalPart: $"csat+{token}", subject: "4", body: "");
        var outcome = await sut.HandleReplyAsync(message, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.TokenInvalid);
        forwarder.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleReplyAsync_ShouldReturnTokenInvalid_WhenTokenExpired()
    {
        // Token issued 8 days ago → past the 7-day TTL evaluated at Now.
        var token = _signer.Sign("ten-42", "conv-1", "resp-1", Now - TimeSpan.FromDays(8));
        var resolver = new FakeResolver { ByToken = DispatchFor("ten-42") };
        var forwarder = new RecordingForwarder();
        var sut = NewHandler(resolver, forwarder);

        var message = BuildReply(replyToLocalPart: $"csat+{token}", subject: "5", body: "");
        var outcome = await sut.HandleReplyAsync(message, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.TokenInvalid);
        forwarder.Calls.Should().BeEmpty();
    }

    // ─── rating regex edge cases ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleReplyAsync_ShouldPreferSubjectRating_WhenBothSubjectAndBodyHaveDigits()
    {
        var token = _signer.Sign("ten-42", "conv-1", "resp-1", Now);
        var sut = NewHandler(new FakeResolver { ByToken = DispatchFor("ten-42") }, out var forwarder);

        var message = BuildReply($"csat+{token}", subject: "Rating: 5", body: "Actually I meant 2");
        var outcome = await sut.HandleReplyAsync(message, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.Captured);
        forwarder.Calls.Should().ContainSingle().Which.Rating.Should().Be(5);
    }

    [Fact]
    public async Task HandleReplyAsync_ShouldFallBackToBodyRating_WhenSubjectHasNoDigit()
    {
        var token = _signer.Sign("ten-42", "conv-1", "resp-1", Now);
        var sut = NewHandler(new FakeResolver { ByToken = DispatchFor("ten-42") }, out var forwarder);

        var message = BuildReply($"csat+{token}", subject: "Re: How did we do?", body: "I'd say 3 overall, thanks.");
        var outcome = await sut.HandleReplyAsync(message, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.Captured);
        forwarder.Calls.Should().ContainSingle().Which.Rating.Should().Be(3);
    }

    [Fact]
    public async Task HandleReplyAsync_ShouldReturnNoRating_WhenNeitherSubjectNorBodyHasOneToFive()
    {
        var token = _signer.Sign("ten-42", "conv-1", "resp-1", Now);
        var sut = NewHandler(new FakeResolver { ByToken = DispatchFor("ten-42") }, out var forwarder);

        var message = BuildReply($"csat+{token}", subject: "Re: How did we do?", body: "It was fine, no complaints.");
        var outcome = await sut.HandleReplyAsync(message, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.NoRating);
        forwarder.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleReplyAsync_ShouldOnlyScanFirst200BodyChars_WhenRatingIsFurtherDown()
    {
        var token = _signer.Sign("ten-42", "conv-1", "resp-1", Now);
        var sut = NewHandler(new FakeResolver { ByToken = DispatchFor("ten-42") }, out var forwarder);

        // A digit only appears after 200 chars of non-digit filler → not scanned → NoRating.
        var filler = new string('x', 250);
        var message = BuildReply($"csat+{token}", subject: "Re: feedback", body: filler + " 5");
        var outcome = await sut.HandleReplyAsync(message, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.NoRating);
        forwarder.Calls.Should().BeEmpty();
    }

    // ─── In-Reply-To fallback ────────────────────────────────────────────────────

    [Fact]
    public async Task HandleReplyAsync_ShouldFallBackToInReplyTo_WhenTokenSuffixStripped()
    {
        var resolver = new FakeResolver { ByInReplyTo = DispatchFor("ten-42") };
        var sut = NewHandler(resolver, out var forwarder);

        // No +token on the Reply-To (forwarder stripped it), but In-Reply-To carries the dispatch id.
        var message = BuildReply(replyToLocalPart: "csat", subject: "4", body: "", inReplyTo: "disp-abc123");
        var outcome = await sut.HandleReplyAsync(message, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.Captured);
        forwarder.Calls.Should().ContainSingle().Which.Rating.Should().Be(4);
        resolver.LastInReplyTo.Should().Be("disp-abc123");
    }

    [Fact]
    public async Task HandleReplyAsync_ShouldReturnNoCorrelation_WhenNoTokenAndNoInReplyToMatch()
    {
        var resolver = new FakeResolver(); // resolves nothing
        var sut = NewHandler(resolver, out var forwarder);

        var message = BuildReply(replyToLocalPart: "csat", subject: "5", body: "", inReplyTo: "unknown-id");
        var outcome = await sut.HandleReplyAsync(message, CancellationToken.None);

        outcome.Should().Be(CsatReplyOutcome.NoCorrelation);
        forwarder.Calls.Should().BeEmpty();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private CsatReplyMailHandler NewHandler(FakeResolver resolver, RecordingForwarder forwarder) =>
        new(_signer, resolver, forwarder, autoReplyEnabled: false, autoReply: null, timeProvider: new FakeTimeProvider(Now));

    private CsatReplyMailHandler NewHandler(FakeResolver resolver, out RecordingForwarder forwarder)
    {
        forwarder = new RecordingForwarder();
        return NewHandler(resolver, forwarder);
    }

    private static CsatEmailDispatch DispatchFor(string tenantId) =>
        new(tenantId, SurveyId: "srv-csat-v1", QueueName: "support-tier1", ConversationId: "conv-1");

    private static MimeMessage BuildReply(
        string replyToLocalPart, string subject, string body, string? inReplyTo = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Visitor", "visitor@example.com"));
        message.To.Add(new MailboxAddress("CSAT", "csat@tenant.verbara.io"));
        message.ReplyTo.Add(new MailboxAddress("CSAT", $"{replyToLocalPart}@tenant.verbara.io"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        if (inReplyTo is not null)
            message.InReplyTo = inReplyTo;
        return message;
    }

    private sealed class FakeResolver : ICsatEmailDispatchResolver
    {
        public CsatEmailDispatch? ByToken { get; init; }
        public CsatEmailDispatch? ByInReplyTo { get; init; }
        public string? LastInReplyTo { get; private set; }

        public Task<CsatEmailDispatch?> ResolveByTokenAsync(string token, CancellationToken ct) =>
            Task.FromResult(ByToken);

        public Task<CsatEmailDispatch?> ResolveByInReplyToAsync(string inReplyToMessageId, CancellationToken ct)
        {
            LastInReplyTo = inReplyToMessageId;
            return Task.FromResult(ByInReplyTo);
        }
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
