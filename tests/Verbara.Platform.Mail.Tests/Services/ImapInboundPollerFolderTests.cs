using MailKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Verbara.Platform.Mail.Services;
using Verbara.Platform.Mail.Tests.Hosting;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// mail-reads-the-configured-imap-folder (#705): the poller reads the folder a mailbox names in
/// <see cref="ImapMailboxOptions.Folder"/>, against a real IMAP server (GreenMail). INBOX stays the default, and a
/// folder the server does not have is a logged error on every poll, never a read of INBOX.
/// </summary>
[Collection("GreenMail")]
public sealed class ImapInboundPollerFolderTests(GreenMailFixture server)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 00, 00, TimeSpan.Zero);
    private readonly HmacCsatReplyTokenSigner _signer = new("test-signing-secret-0123456789abcdef", TimeSpan.FromDays(7));

    [Theory]
    [InlineData(false)] // a folder at the top of the personal namespace
    [InlineData(true)] // a folder under INBOX, named with the server's own separator
    public async Task PollMailboxAsync_ShouldForwardTheRepliesOfTheConfiguredFolderOnly_WhenFolderIsNotInbox(bool underInbox)
    {
        var user = NewUser();
        string folderName;
        using (var setup = await server.ConnectAsync(user))
        {
            var parent = (underInbox ? setup.Inbox : setup.GetFolder(setup.PersonalNamespaces[0]))
                ?? throw new InvalidOperationException("The IMAP server exposed no parent folder.");
            var created = await parent.CreateAsync("CsatReplies", isMessageFolder: true)
                ?? throw new InvalidOperationException("The IMAP server created no folder.");
            folderName = created.FullName;
            await setup.DisconnectAsync(quit: true);
        }

        var mailbox = Mailbox(user, folderName);
        var forwarder = new ImapInboundPollerForwardOnceTests.RecordingForwarder();
        var state = new ImapInboundPollerForwardOnceTests.InMemoryPositionStore();
        var poller = NewPoller(forwarder, state);
        await poller.PollMailboxAsync(mailbox, CancellationToken.None); // first read: both folders empty

        await AppendReplyAsync(user, folderName, rating: 5);
        await AppendReplyAsync(user, "INBOX", rating: 1);
        var processed = await poller.PollMailboxAsync(mailbox, CancellationToken.None);

        processed.Should().Be(1);
        forwarder.Forwarded.Should().Equal(5);
    }

    [Theory]
    [InlineData("INBOX")]
    [InlineData("inbox")] // INBOX is case-insensitive (RFC 3501 5.1)
    [InlineData("")] // a blank setting read INBOX before #705 and still does
    public async Task PollMailboxAsync_ShouldReadInbox_WhenFolderNamesInboxOrIsBlank(string folder)
    {
        var user = NewUser();
        var mailbox = Mailbox(user, folder);
        var forwarder = new ImapInboundPollerForwardOnceTests.RecordingForwarder();
        var poller = NewPoller(forwarder, new ImapInboundPollerForwardOnceTests.InMemoryPositionStore());
        await poller.PollMailboxAsync(mailbox, CancellationToken.None);

        await AppendReplyAsync(user, "INBOX", rating: 4);
        await poller.PollMailboxAsync(mailbox, CancellationToken.None);

        forwarder.Forwarded.Should().Equal(4);
    }

    [Fact]
    public async Task PollMailboxAsync_ShouldReadInbox_WhenFolderIsNotSet()
    {
        var user = NewUser();
        var mailbox = new ImapMailboxOptions
        {
            TenantId = "ten-42", Host = server.Host, Port = server.Port, UseTls = false,
            Username = user, Password = "throwaway",
        };
        var forwarder = new ImapInboundPollerForwardOnceTests.RecordingForwarder();
        var state = new ImapInboundPollerForwardOnceTests.InMemoryPositionStore();
        var poller = NewPoller(forwarder, state);
        await poller.PollMailboxAsync(mailbox, CancellationToken.None);

        await AppendReplyAsync(user, "INBOX", rating: 3);
        await poller.PollMailboxAsync(mailbox, CancellationToken.None);

        mailbox.Folder.Should().Be("INBOX"); // the poll-position key an existing deployment already stored
        forwarder.Forwarded.Should().Equal(3);
    }

    [Fact]
    public async Task PollMailboxAsync_ShouldThrowFolderNotFoundAndReadNothing_WhenTheFolderDoesNotExist()
    {
        var user = NewUser();
        await AppendReplyAsync(user, "INBOX", rating: 2);
        var mailbox = Mailbox(user, "NoSuchFolder");
        var forwarder = new ImapInboundPollerForwardOnceTests.RecordingForwarder();
        var state = new ImapInboundPollerForwardOnceTests.InMemoryPositionStore();
        var poller = NewPoller(forwarder, state);

        var poll = () => poller.PollMailboxAsync(mailbox, CancellationToken.None);

        await poll.Should().ThrowAsync<FolderNotFoundException>();
        state.Saves.Should().Be(0); // INBOX was not read as a stand-in: no position was taken from it
        forwarder.Forwarded.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldLogAnErrorNamingTheFolderOnEveryPoll_WhenTheFolderDoesNotExist()
    {
        var user = NewUser();
        var capture = new LogRecordCapture();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));
        var options = new ImapPollerOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(200) };
        options.Mailboxes.Add(Mailbox(user, "NoSuchFolder"));
        var poller = new ImapInboundPoller(
            Options.Create(options),
            NewHandler(new ImapInboundPollerForwardOnceTests.RecordingForwarder()),
            new ImapInboundPollerForwardOnceTests.InMemoryPositionStore(),
            factory.CreateLogger<ImapInboundPoller>());

        await poller.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (FolderErrors(capture).Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(100); // fence-allow: LOOP-DRIVER — bounded poll on the captured log, which raises no signal of its own
        await poller.StopAsync(CancellationToken.None);

        var errors = FolderErrors(capture);
        errors.Should().HaveCountGreaterThanOrEqualTo(2, "the poller keeps running and reports the folder on each poll");
        errors.Should().OnlyContain(r => r.Level == LogLevel.Error && r.Message.Contains("ten-42", StringComparison.Ordinal));
    }

    private static List<CapturedLogRecord> FolderErrors(LogRecordCapture capture) =>
        [.. capture.Records.Where(r => r.Category == typeof(ImapInboundPoller).FullName
                                       && r.Message.Contains("folder=NoSuchFolder", StringComparison.Ordinal))];

    private ImapMailboxOptions Mailbox(string user, string folder) => new()
    {
        TenantId = "ten-42", Host = server.Host, Port = server.Port, UseTls = false,
        Username = user, Password = "throwaway", Folder = folder,
    };

    private static string NewUser() => $"csat-{Guid.NewGuid():N}";

    private async Task AppendReplyAsync(string user, string folder, int rating)
    {
        using var reply = new MimeMessage();
        reply.From.Add(new MailboxAddress("Visitor", "visitor@example.com"));
        reply.To.Add(new MailboxAddress("CSAT", "csat@tenant.verbara.io"));
        reply.Subject = $"Re: How did we do? {rating}";
        reply.Body = new TextPart("plain") { Text = "" };
        reply.InReplyTo = "disp-1";

        using var client = await server.ConnectAsync(user);
        var target = await client.GetFolderAsync(folder);
        await target.AppendAsync(reply);
        await client.DisconnectAsync(quit: true);
    }

    private ImapInboundPoller NewPoller(
        ImapInboundPollerForwardOnceTests.RecordingForwarder forwarder,
        ImapInboundPollerForwardOnceTests.InMemoryPositionStore state) =>
        new(Options.Create(new ImapPollerOptions { Enabled = true }), NewHandler(forwarder), state,
            NullLogger<ImapInboundPoller>.Instance);

    private CsatReplyMailHandler NewHandler(ImapInboundPollerForwardOnceTests.RecordingForwarder forwarder) =>
        new(_signer, new InReplyToResolver(), forwarder, timeProvider: new FixedTime(Now));

    private sealed class InReplyToResolver : ICsatEmailDispatchResolver
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
