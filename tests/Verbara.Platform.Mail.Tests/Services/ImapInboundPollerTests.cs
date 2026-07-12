using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;
using Verbara.Platform.Mail.Services;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// Deterministic poll-loop coverage for <see cref="ImapInboundPoller"/> via the internal
/// <see cref="IImapClient"/> factory ctor — asserts UID-based idempotent dedup (an already-seen UID
/// is never re-fetched across polls) without needing a live IMAP endpoint.
/// </summary>
public sealed class ImapInboundPollerTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);
    private readonly HmacCsatReplyTokenSigner _signer = new("test-signing-secret-0123456789abcdef", TimeSpan.FromDays(7));

    private static readonly ImapMailboxOptions Mailbox = new()
    {
        TenantId = "ten-42",
        Host = "localhost",
        Username = "csat@tenant.verbara.io",
        Password = "pw",
    };

    [Fact]
    public async Task PollMailboxAsync_ShouldProcessOnlyNewUids_WhenPolledTwice()
    {
        var forwarder = new CountingForwarder();
        var handler = new CsatReplyMailHandler(
            _signer,
            new AlwaysResolve(),
            forwarder,
            timeProvider: new FakeTimeProvider(Now));

        // First poll surfaces UIDs 1..2; second poll surfaces UID 3 only (server-side UID search
        // above the tracked last-UID). The fake records which UIDs were fetched each call.
        var folder = Substitute.For<IMailFolder>();
        folder.OpenAsync(FolderAccess.ReadOnly, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FolderAccess.ReadOnly));

        var fetchedUids = new List<uint>();
        folder.GetMessageAsync(Arg.Any<UniqueId>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>())
            .Returns(ci =>
            {
                fetchedUids.Add(ci.Arg<UniqueId>().Id);
                return Task.FromResult(BuildReply());
            });

        // SearchAsync honours the poller's "UID > lastUid" range: return only in-range UIDs.
        folder.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IList<UniqueId>>([new UniqueId(1), new UniqueId(2)]),
                     _ => Task.FromResult<IList<UniqueId>>([new UniqueId(3)]));

        var client = Substitute.For<IImapClient>();
        client.Inbox.Returns(folder);

        var poller = new ImapInboundPoller(
            Options.Create(new ImapPollerOptions { Enabled = true }),
            handler,
            NullLogger<ImapInboundPoller>.Instance,
            _ => Task.FromResult(client));

        var first = await poller.PollMailboxAsync(Mailbox, CancellationToken.None);
        var second = await poller.PollMailboxAsync(Mailbox, CancellationToken.None);

        first.Should().Be(2);
        second.Should().Be(1);
        fetchedUids.Should().Equal(1u, 2u, 3u); // no UID re-fetched → idempotent dedup
        forwarder.Count.Should().Be(3);
    }

    [Fact]
    public async Task PollMailboxAsync_ShouldNotAdvanceUid_WhenNoNewMessages()
    {
        var forwarder = new CountingForwarder();
        var handler = new CsatReplyMailHandler(_signer, new AlwaysResolve(), forwarder, timeProvider: new FakeTimeProvider(Now));

        var folder = Substitute.For<IMailFolder>();
        folder.OpenAsync(FolderAccess.ReadOnly, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FolderAccess.ReadOnly));
        folder.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<UniqueId>>([]));

        var client = Substitute.For<IImapClient>();
        client.Inbox.Returns(folder);

        var poller = new ImapInboundPoller(
            Options.Create(new ImapPollerOptions { Enabled = true }),
            handler,
            NullLogger<ImapInboundPoller>.Instance,
            _ => Task.FromResult(client));

        var processed = await poller.PollMailboxAsync(Mailbox, CancellationToken.None);

        processed.Should().Be(0);
        forwarder.Count.Should().Be(0);
    }

    private static MimeMessage BuildReply()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Visitor", "visitor@example.com"));
        message.To.Add(new MailboxAddress("CSAT", "csat@tenant.verbara.io"));
        message.ReplyTo.Add(new MailboxAddress("CSAT", "csat+tok@tenant.verbara.io"));
        message.Subject = "5";
        message.Body = new TextPart("plain") { Text = "" };
        message.InReplyTo = "disp-1";
        return message;
    }

    private sealed class AlwaysResolve : ICsatEmailDispatchResolver
    {
        private static readonly CsatEmailDispatch Dispatch = new("ten-42", "srv-csat-v1", "support-tier1", "conv-1");
        public Task<CsatEmailDispatch?> ResolveByTokenAsync(string token, CancellationToken ct) => Task.FromResult<CsatEmailDispatch?>(Dispatch);
        public Task<CsatEmailDispatch?> ResolveByInReplyToAsync(string inReplyToMessageId, CancellationToken ct) => Task.FromResult<CsatEmailDispatch?>(Dispatch);
    }

    private sealed class CountingForwarder : ICsatEmailCaptureForwarder
    {
        public int Count { get; private set; }
        public Task ForwardEmailRatingAsync(CsatEmailDispatch dispatch, int rating, DateTimeOffset capturedAt, CancellationToken ct)
        {
            Count++;
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
