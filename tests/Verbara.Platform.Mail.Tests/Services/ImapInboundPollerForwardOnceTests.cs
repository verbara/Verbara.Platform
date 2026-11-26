using System.Net;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Mail.Services;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// csat-email-replies-forwarded-once (#696): every reply in the mailbox reaches the Api once, across a restart of the
/// mail service and across a poll in which one message fails. A "restart" is a new poller instance over the same
/// mailbox, the same way the process starts again with nothing in memory.
/// </summary>
public sealed class ImapInboundPollerForwardOnceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 00, 00, TimeSpan.Zero);
    private readonly HmacCsatReplyTokenSigner _signer = new("test-signing-secret-0123456789abcdef", TimeSpan.FromDays(7));

    private static readonly ImapMailboxOptions Mailbox = new()
    {
        TenantId = "ten-42",
        Host = "localhost",
        Username = "csat@tenant.verbara.io",
        Password = "pw",
    };

    [Fact]
    public async Task PollMailboxAsync_ShouldForwardAReplyOnce_WhenTheMailServiceRestartsWithTheReplyStillInTheMailbox()
    {
        var server = new FakeMailbox(uidValidity: 7);
        var forwarder = new RecordingForwarder();
        var state = new InMemoryPositionStore();

        await NewPoller(forwarder, server, state).PollMailboxAsync(Mailbox, CancellationToken.None); // empty mailbox
        server.Deliver(rating: 4);
        await NewPoller(forwarder, server, state).PollMailboxAsync(Mailbox, CancellationToken.None);

        // Two restarts, each followed by a poll, with the reply still in the mailbox.
        await NewPoller(forwarder, server, state).PollMailboxAsync(Mailbox, CancellationToken.None);
        await NewPoller(forwarder, server, state).PollMailboxAsync(Mailbox, CancellationToken.None);

        forwarder.Forwarded.Should().Equal(4);
    }

    [Fact]
    public async Task PollMailboxAsync_ShouldNotForwardAgainTheRepliesBeforeAFailingOne_WhenTheNextPollRetries()
    {
        var server = new FakeMailbox(uidValidity: 7);
        var forwarder = new RecordingForwarder();
        var state = new InMemoryPositionStore();
        var poller = NewPoller(forwarder, server, state);
        await poller.PollMailboxAsync(Mailbox, CancellationToken.None); // empty mailbox
        server.Deliver(rating: 1);
        server.Deliver(rating: 2);
        server.Deliver(rating: 3);
        forwarder.FailOnce(rating: 2, HttpStatusCode.ServiceUnavailable);

        await PollIgnoringErrorsAsync(poller);
        await PollIgnoringErrorsAsync(poller);

        forwarder.Forwarded.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task PollMailboxAsync_ShouldForwardNoReplyAlreadyInTheMailbox_WhenTheMailboxIsReadForTheFirstTime()
    {
        // An existing deployment upgrades: its mailbox holds replies the previous version already forwarded.
        var server = new FakeMailbox(uidValidity: 7);
        server.Deliver(rating: 1);
        server.Deliver(rating: 2);
        var forwarder = new RecordingForwarder();
        var state = new InMemoryPositionStore();

        var first = await NewPoller(forwarder, server, state).PollMailboxAsync(Mailbox, CancellationToken.None);
        server.Deliver(rating: 3);
        var second = await NewPoller(forwarder, server, state).PollMailboxAsync(Mailbox, CancellationToken.None);

        first.Should().Be(0);
        second.Should().Be(1);
        forwarder.Forwarded.Should().Equal(3);
        state.Position.Should().Be(new ImapPollPosition(7, 3));
    }

    [Fact]
    public async Task PollMailboxAsync_ShouldStartFromTheNewestMessage_WhenTheServerRenumberedTheMailbox()
    {
        var server = new FakeMailbox(uidValidity: 8);
        server.Deliver(rating: 1);
        server.Deliver(rating: 2);
        var forwarder = new RecordingForwarder();
        var state = new InMemoryPositionStore { Position = new ImapPollPosition(7, 0) };

        var processed = await NewPoller(forwarder, server, state).PollMailboxAsync(Mailbox, CancellationToken.None);

        processed.Should().Be(0);
        forwarder.Forwarded.Should().BeEmpty();
        state.Position.Should().Be(new ImapPollPosition(8, 2));
    }

    [Fact]
    public async Task PollMailboxAsync_ShouldStoreThePositionAfterEachMessage_WhenAMessageFails()
    {
        var server = new FakeMailbox(uidValidity: 7);
        var forwarder = new RecordingForwarder();
        var state = new InMemoryPositionStore();
        var poller = NewPoller(forwarder, server, state);
        await poller.PollMailboxAsync(Mailbox, CancellationToken.None);
        server.Deliver(rating: 1);
        server.Deliver(rating: 2);
        server.Deliver(rating: 3);
        forwarder.FailOnce(rating: 2, HttpStatusCode.ServiceUnavailable);

        var processed = await poller.PollMailboxAsync(Mailbox, CancellationToken.None);

        processed.Should().Be(1);
        state.Position.Should().Be(new ImapPollPosition(7, 1));

        // A restart between the failure and the retry still forwards the first reply once.
        await NewPoller(forwarder, server, state).PollMailboxAsync(Mailbox, CancellationToken.None);
        forwarder.Forwarded.Should().Equal(1, 2, 3);
        state.Position.Should().Be(new ImapPollPosition(7, 3));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task PollMailboxAsync_ShouldPassAMessageAtOnceAndForwardTheNext_WhenTheApiRefusesItsContent(
        HttpStatusCode status)
    {
        var server = new FakeMailbox(uidValidity: 7);
        var forwarder = new RecordingForwarder();
        var state = new InMemoryPositionStore();
        var poller = NewPoller(forwarder, server, state);
        await poller.PollMailboxAsync(Mailbox, CancellationToken.None);
        server.Deliver(rating: 1);
        server.Deliver(rating: 2);
        server.Deliver(rating: 3);
        forwarder.FailAlways(rating: 2, status);

        var first = await poller.PollMailboxAsync(Mailbox, CancellationToken.None);
        var second = await poller.PollMailboxAsync(Mailbox, CancellationToken.None);

        first.Should().Be(3);
        second.Should().Be(0);
        forwarder.Forwarded.Should().Equal(1, 3);
        forwarder.Rejected.Should().Equal(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public async Task PollMailboxAsync_ShouldRetryAMessageThenPassIt_WhenTheApiKeepsFailingOnIt(HttpStatusCode status)
    {
        var server = new FakeMailbox(uidValidity: 7);
        var forwarder = new RecordingForwarder();
        var state = new InMemoryPositionStore();
        var poller = NewPoller(forwarder, server, state, maxAttempts: 3);
        await poller.PollMailboxAsync(Mailbox, CancellationToken.None);
        server.Deliver(rating: 1);
        server.Deliver(rating: 2);
        server.Deliver(rating: 3);
        forwarder.FailAlways(rating: 2, status);

        var polls = new List<int>();
        for (var i = 0; i < 4; i++)
            polls.Add(await poller.PollMailboxAsync(Mailbox, CancellationToken.None));

        polls.Should().Equal(1, 0, 2, 0);
        forwarder.Rejected.Should().Equal(2, 2, 2);
        forwarder.Forwarded.Should().Equal(1, 3);
        state.Position.Should().Be(new ImapPollPosition(7, 3));
    }

    // The Api is down, not licensed or misconfigured: no failed attempt belongs to the message, so the reply waits for
    // the Api however long that takes, and nothing behind it is passed or forwarded out of order.
    public static TheoryData<string> UnavailableAnswers =>
    [
        "no-answer", "timeout",
        nameof(HttpStatusCode.PaymentRequired), nameof(HttpStatusCode.Unauthorized), nameof(HttpStatusCode.Forbidden),
        nameof(HttpStatusCode.NotFound), nameof(HttpStatusCode.RequestTimeout), nameof(HttpStatusCode.TooManyRequests),
        nameof(HttpStatusCode.BadGateway), nameof(HttpStatusCode.ServiceUnavailable), nameof(HttpStatusCode.GatewayTimeout),
    ];

    [Theory]
    [MemberData(nameof(UnavailableAnswers))]
    public async Task PollMailboxAsync_ShouldKeepAMessageUntilTheApiAnswers_WhenTheApiIsUnavailable(string answer)
    {
        var server = new FakeMailbox(uidValidity: 7);
        var forwarder = new RecordingForwarder();
        var state = new InMemoryPositionStore();
        var poller = NewPoller(forwarder, server, state, maxAttempts: 3);
        await poller.PollMailboxAsync(Mailbox, CancellationToken.None);
        server.Deliver(rating: 1);
        server.Deliver(rating: 2);
        server.Deliver(rating: 3);
        forwarder.FailAlways(rating: 2, answer);

        for (var i = 0; i < 10; i++)
            await poller.PollMailboxAsync(Mailbox, CancellationToken.None);
        forwarder.Recover(rating: 2);
        var afterRecovery = await poller.PollMailboxAsync(Mailbox, CancellationToken.None);

        forwarder.Rejected.Should().HaveCount(10);
        afterRecovery.Should().Be(2);
        forwarder.Forwarded.Should().Equal(1, 2, 3);
        state.Position.Should().Be(new ImapPollPosition(7, 3));
    }

    [Fact]
    public async Task PollMailboxAsync_ShouldForwardAReplyOnce_WhenTheAcknowledgementEmailFails()
    {
        var server = new FakeMailbox(uidValidity: 7);
        var forwarder = new RecordingForwarder();
        var state = new InMemoryPositionStore();
        var autoReply = new FailingEmailService();
        var poller = NewPoller(forwarder, server, state, autoReply: autoReply);
        await poller.PollMailboxAsync(Mailbox, CancellationToken.None);
        server.Deliver(rating: 5);

        await poller.PollMailboxAsync(Mailbox, CancellationToken.None);
        await poller.PollMailboxAsync(Mailbox, CancellationToken.None);

        forwarder.Forwarded.Should().Equal(5);
        autoReply.Attempts.Should().Be(1);
    }

    // The background loop logs a failed poll and polls again; the test does the same (on the unfixed poller a failing
    // message failed the whole poll).
    private static Task<Exception?> PollIgnoringErrorsAsync(ImapInboundPoller poller) =>
        Record.ExceptionAsync(() => poller.PollMailboxAsync(Mailbox, CancellationToken.None));

    private ImapInboundPoller NewPoller(
        RecordingForwarder forwarder, FakeMailbox server, InMemoryPositionStore state, int maxAttempts = 20,
        IEmailService? autoReply = null)
    {
        var handler = new CsatReplyMailHandler(
            _signer, new AlwaysResolve(), forwarder,
            autoReplyEnabled: autoReply is not null, autoReply: autoReply, timeProvider: new FixedTime(Now));
        return new ImapInboundPoller(
            Options.Create(new ImapPollerOptions { Enabled = true, MaxAttemptsPerMessage = maxAttempts }),
            handler,
            state,
            NullLogger<ImapInboundPoller>.Instance,
            _ => Task.FromResult(server.Connect()));
    }

    /// <summary>An IMAP inbox held in memory: UIDs ascend from 1, SEARCH honours a UID range.</summary>
    internal sealed class FakeMailbox(uint uidValidity)
    {
        private readonly SortedDictionary<uint, MimeMessage> _messages = [];

        public uint UidValidity { get; set; } = uidValidity;

        public void Deliver(int rating)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Visitor", "visitor@example.com"));
            message.To.Add(new MailboxAddress("CSAT", "csat@tenant.verbara.io"));
            message.Subject = $"Re: How did we do? {rating}";
            message.Body = new TextPart("plain") { Text = "" };
            message.InReplyTo = "disp-1";
            _messages[(uint)_messages.Count + 1] = message;
        }

        public IImapClient Connect()
        {
            var folder = Substitute.For<IMailFolder>();
            folder.OpenAsync(Arg.Any<FolderAccess>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(FolderAccess.ReadOnly));
            folder.UidValidity.Returns(_ => UidValidity);
            folder.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var query = ci.Arg<SearchQuery>();
                    IList<UniqueId> hits = _messages.Keys
                        .Select(uid => new UniqueId(UidValidity, uid))
                        .Where(uid => query is not UidSearchQuery byUid || byUid.Uids.Contains(uid))
                        .ToList();
                    return Task.FromResult(hits);
                });
            folder.GetMessageAsync(Arg.Any<UniqueId>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>())
                .Returns(ci => Task.FromResult(_messages[ci.Arg<UniqueId>().Id]));

            var client = Substitute.For<IImapClient>();
            client.Inbox.Returns(folder);
            return client;
        }
    }

    /// <summary>The durable position store, held in memory and shared across "restarts" (poller instances).</summary>
    internal sealed class InMemoryPositionStore : IImapPollPositionStore
    {
        public ImapPollPosition? Position { get; set; }

        public int Saves { get; private set; }

        public Task<ImapPollPosition?> GetAsync(ImapMailboxOptions mailbox, CancellationToken ct) => Task.FromResult(Position);

        public Task SaveAsync(ImapMailboxOptions mailbox, ImapPollPosition position, CancellationToken ct)
        {
            Position = position;
            Saves++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Records each rating the Api accepted; can answer an error status for a given rating.</summary>
    internal sealed class RecordingForwarder : ICsatEmailCaptureForwarder
    {
        private readonly Dictionary<int, (string Answer, int Times)> _failures = [];

        public List<int> Forwarded { get; } = [];

        public List<int> Rejected { get; } = [];

        public void FailOnce(int rating, HttpStatusCode status) => _failures[rating] = (status.ToString(), 1);

        public void FailAlways(int rating, HttpStatusCode status) => _failures[rating] = (status.ToString(), int.MaxValue);

        /// <summary>
        /// Fails with an HTTP status named as in <see cref="HttpStatusCode"/>, or with no answer at all: <c>no-answer</c>
        /// (connection refused, as HttpClient throws it) or <c>timeout</c> (the HttpClient timeout).
        /// </summary>
        public void FailAlways(int rating, string answer) => _failures[rating] = (answer, int.MaxValue);

        public void Recover(int rating) => _failures.Remove(rating);

        public Task ForwardEmailRatingAsync(CsatEmailDispatch dispatch, int rating, DateTimeOffset capturedAt, CancellationToken ct)
        {
            if (_failures.TryGetValue(rating, out var failure) && failure.Times > 0)
            {
                _failures[rating] = (failure.Answer, failure.Times == int.MaxValue ? int.MaxValue : failure.Times - 1);
                Rejected.Add(rating);
                throw failure.Answer switch
                {
                    "no-answer" => new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (api:8080)"),
                    "timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
                        new TimeoutException("The operation was canceled.")),
                    _ => StatusError(Enum.Parse<HttpStatusCode>(failure.Answer)),
                };
            }

            Forwarded.Add(rating);
            return Task.CompletedTask;
        }

        private static HttpRequestException StatusError(HttpStatusCode status) =>
            new($"Response status code does not indicate success: {(int)status}.", inner: null, statusCode: status);
    }

    private sealed class FailingEmailService : IEmailService
    {
        public int Attempts { get; private set; }

        public ValueTask SendAsync(EmailMessage message, CancellationToken ct)
        {
            Attempts++;
            throw new InvalidOperationException("SMTP down");
        }
    }

    private sealed class AlwaysResolve : ICsatEmailDispatchResolver
    {
        private static readonly CsatEmailDispatch Dispatch = new("ten-42", "srv-csat-v1", "support-tier1", "conv-1");
        public Task<CsatEmailDispatch?> ResolveByTokenAsync(string token, CancellationToken ct) => Task.FromResult<CsatEmailDispatch?>(Dispatch);
        public Task<CsatEmailDispatch?> ResolveByInReplyToAsync(string inReplyToMessageId, CancellationToken ct) => Task.FromResult<CsatEmailDispatch?>(Dispatch);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
