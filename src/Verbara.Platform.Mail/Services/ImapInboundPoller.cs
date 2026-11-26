using System.Collections.Concurrent;
using System.Net;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Mail.Services;

/// <summary>
/// Background poller that drains each configured per-tenant <c>csat@…</c> IMAP mailbox on a fixed
/// interval (csat-runner Phase C, spec: "IMAP gap-fill"). Because inbound mail is not reachable
/// in-process, this service fills the gap: every <see cref="ImapPollerOptions.PollInterval"/> it
/// connects to each mailbox, fetches messages whose UID is greater than the last one it handled,
/// parses each into a MailKit <see cref="MimeKit.MimeMessage"/>, and hands it to
/// <see cref="CsatReplyMailHandler"/> for token verification, rating extraction, and forwarding.
/// <para>
/// The last handled UID and the mailbox's UIDVALIDITY live in <see cref="IImapPollPositionStore"/>
/// (<c>mail.imap_poll_positions</c>), written after each message, so a reply is forwarded once across
/// restarts of the mail service and across a poll in which a later message fails (#696). The mailbox is
/// opened read-only: the poller never changes a flag or moves a message, so a person can read the same
/// mailbox and the login needs no write access. With no stored position (the first read after an
/// upgrade or on a new deployment) or a new UIDVALIDITY, the newest message becomes the starting point
/// and no older message is forwarded.
/// </para>
/// </summary>
public sealed partial class ImapInboundPoller : BackgroundService
{
    private readonly ImapPollerOptions _options;
    private readonly CsatReplyMailHandler _replyHandler;
    private readonly IImapPollPositionStore _positions;
    private readonly ILogger<ImapInboundPoller> _logger;
    private readonly Func<CancellationToken, Task<IImapClient>>? _clientFactory;

    // Failed attempts of the message at the head of each mailbox (keyed by mailbox + UIDVALIDITY + UID).
    // Kept in memory: a restart gives a failing message a fresh count, which stays bounded.
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public ImapInboundPoller(
        IOptions<ImapPollerOptions> options,
        CsatReplyMailHandler replyHandler,
        IImapPollPositionStore positions,
        ILogger<ImapInboundPoller> logger)
    {
        _options = options.Value;
        _replyHandler = replyHandler;
        _positions = positions;
        _logger = logger;
        _clientFactory = null;
    }

    /// <summary>
    /// Test-only constructor injecting an <see cref="IImapClient"/> factory so the poll loop can be
    /// exercised against a MailHog / fake IMAP endpoint without the production connect path.
    /// </summary>
    internal ImapInboundPoller(
        IOptions<ImapPollerOptions> options,
        CsatReplyMailHandler replyHandler,
        IImapPollPositionStore positions,
        ILogger<ImapInboundPoller> logger,
        Func<CancellationToken, Task<IImapClient>> clientFactory)
    {
        _options = options.Value;
        _replyHandler = replyHandler;
        _positions = positions;
        _logger = logger;
        _clientFactory = clientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled();
            return;
        }

        using var timer = new PeriodicTimer(_options.PollInterval);
        do
        {
            foreach (var mailbox in _options.Mailboxes)
            {
                try
                {
                    await PollMailboxAsync(mailbox, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // one flaky mailbox must not abort the whole poller loop
                catch (Exception ex)
                {
                    LogPollError(mailbox.TenantId, mailbox.Host, ex);
                }
#pragma warning restore CA1031
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Drains one mailbox: connects, reads the stored position, hands each message with a UID above it
    /// to the reply handler in UID order, and stores the position after each one. A message that fails
    /// stops the drain, so the next poll starts at that message. While the Api is unavailable (no answer,
    /// not licensed, a service-key mismatch, 502/503/504 ...) the message waits however long that takes;
    /// one the Api refuses for its content, or one that failed on its own
    /// <see cref="ImapPollerOptions.MaxAttemptsPerMessage"/> times, is logged and passed.
    /// Public so a host or an integration test can trigger a single deterministic drain without waiting
    /// on the timer. Returns the number of messages handled.
    /// </summary>
    public async Task<int> PollMailboxAsync(ImapMailboxOptions mailbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        var client = await ConnectAsync(mailbox, ct).ConfigureAwait(false);
        try
        {
            var inbox = client.Inbox
                ?? throw new InvalidOperationException("IMAP client exposed no Inbox folder.");
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            var uidValidity = inbox.UidValidity;
            var stored = await _positions.GetAsync(mailbox, ct).ConfigureAwait(false);
            if (stored is null || stored.UidValidity != uidValidity)
            {
                await StartFromNewestAsync(mailbox, inbox, uidValidity, stored, ct).ConfigureAwait(false);
                return 0;
            }

            var lastUid = stored.LastUid;
            var query = SearchQuery.Uids(new UniqueIdRange(new UniqueId(uidValidity, lastUid + 1), UniqueId.MaxValue));
            var uids = await inbox.SearchAsync(query, ct).ConfigureAwait(false);

            var processed = 0;
            foreach (var uid in uids.Where(u => u.Id > lastUid).OrderBy(u => u.Id))
            {
                if (!await HandleOneAsync(mailbox, inbox, uidValidity, uid, ct).ConfigureAwait(false))
                    break; // retried from this message on the next poll

                await _positions.SaveAsync(mailbox, new ImapPollPosition(uidValidity, uid.Id), ct).ConfigureAwait(false);
                processed++;
            }

            LogPolled(mailbox.TenantId, processed);
            return processed;
        }
        finally
        {
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
            client.Dispose();
        }
    }

    // True when the message is done with (handled, or passed after a refusal or too many failures);
    // false when it failed and the next poll should try it again.
    private async Task<bool> HandleOneAsync(
        ImapMailboxOptions mailbox, IMailFolder inbox, uint uidValidity, UniqueId uid, CancellationToken ct)
    {
        var attemptKey = $"{MailboxKey(mailbox)}:{uidValidity}:{uid.Id}";
        try
        {
            var message = await inbox.GetMessageAsync(uid, ct).ConfigureAwait(false);
            var outcome = await _replyHandler.HandleReplyAsync(message, ct).ConfigureAwait(false);
            _attempts.TryRemove(attemptKey, out _);
            LogHandled(mailbox.TenantId, uid.Id, outcome);
            return true;
        }
#pragma warning disable CA1031 // a failing message is logged and retried or passed; it must not stop the mailbox for good
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (IsRefusedForItsContent(ex))
            {
                _attempts.TryRemove(attemptKey, out _);
                LogMessageRefused(mailbox.TenantId, uid.Id, ex);
                return true;
            }

            if (IsApiUnavailable(ex))
            {
                LogApiUnavailable(mailbox.TenantId, uid.Id, ex);
                return false;
            }

            var attempts = _attempts.AddOrUpdate(attemptKey, 1, (_, n) => n + 1);
            if (attempts >= Math.Max(1, _options.MaxAttemptsPerMessage))
            {
                _attempts.TryRemove(attemptKey, out _);
                LogMessageGivenUp(mailbox.TenantId, uid.Id, attempts, ex);
                return true;
            }

            LogMessageFailed(mailbox.TenantId, uid.Id, attempts, ex);
            return false;
        }
#pragma warning restore CA1031
    }

    // The Api answered that this request can never succeed as sent: the only answers its capture endpoint gives
    // for the content of a reply. Every other answer is retried.
    private static bool IsRefusedForItsContent(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Conflict
            or HttpStatusCode.UnprocessableEntity };

    // The Api was not reached, or answered for its own state (not licensed, a service-key or routing mismatch,
    // overloaded, down behind a proxy), never for this message. Such a failure is not counted against the message:
    // an outage of any length holds the mailbox and then resumes, instead of passing one reply after another.
    private static bool IsApiUnavailable(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.PaymentRequired
            or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        OperationCanceledException { InnerException: TimeoutException } => true, // the HttpClient timeout
        _ => false,
    };

    private async Task StartFromNewestAsync(
        ImapMailboxOptions mailbox, IMailFolder inbox, uint uidValidity, ImapPollPosition? stored, CancellationToken ct)
    {
        var all = await inbox.SearchAsync(SearchQuery.All, ct).ConfigureAwait(false);
        var newest = all.Count == 0 ? 0u : all.Max(u => u.Id);
        await _positions.SaveAsync(mailbox, new ImapPollPosition(uidValidity, newest), ct).ConfigureAwait(false);
        if (stored is null)
            LogStartedFromNewest(mailbox.TenantId, all.Count, newest);
        else
            LogUidValidityChanged(mailbox.TenantId, stored.UidValidity, uidValidity, all.Count, newest);
    }

    private async Task<IImapClient> ConnectAsync(ImapMailboxOptions mailbox, CancellationToken ct)
    {
        if (_clientFactory is not null)
            return await _clientFactory(ct).ConfigureAwait(false);

        var client = new ImapClient();
        var socketOptions = mailbox.UseTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None;
        await client.ConnectAsync(mailbox.Host, mailbox.Port, socketOptions, ct).ConfigureAwait(false);
        await client.AuthenticateAsync(mailbox.Username, mailbox.Password, ct).ConfigureAwait(false);
        return client;
    }

    private static string MailboxKey(ImapMailboxOptions m) => $"{m.TenantId}:{m.Host}:{m.Port}:{m.Username}:{m.Folder}";

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP inbound poller disabled (Imap:Enabled=false)")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP mailbox drained: tenant={TenantId} processed={Processed}")]
    private partial void LogPolled(string tenantId, int processed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IMAP poll failed: tenant={TenantId} host={Host}")]
    private partial void LogPollError(string tenantId, string host, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP message handled: tenant={TenantId} uid={Uid} outcome={Outcome}")]
    private partial void LogHandled(string tenantId, uint uid, CsatReplyOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IMAP message failed, retried on the next poll: tenant={TenantId} uid={Uid} attempt={Attempt}")]
    private partial void LogMessageFailed(string tenantId, uint uid, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IMAP message waits for the Api, retried on the next poll: tenant={TenantId} uid={Uid}")]
    private partial void LogApiUnavailable(string tenantId, uint uid, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "IMAP message passed after {Attempts} failed attempts, its reply is not recorded: tenant={TenantId} uid={Uid}")]
    private partial void LogMessageGivenUp(string tenantId, uint uid, int attempts, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "IMAP message refused by the Api and passed, its reply is not recorded: tenant={TenantId} uid={Uid}")]
    private partial void LogMessageRefused(string tenantId, uint uid, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP mailbox read for the first time: tenant={TenantId} starts after uid={NewestUid}, {Skipped} message(s) already in the mailbox not forwarded")]
    private partial void LogStartedFromNewest(string tenantId, int skipped, uint newestUid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IMAP mailbox renumbered (UIDVALIDITY {Before} -> {After}): tenant={TenantId} starts after uid={NewestUid}, {Skipped} message(s) in the mailbox not forwarded")]
    private partial void LogUidValidityChanged(string tenantId, uint before, uint after, int skipped, uint newestUid);
}
