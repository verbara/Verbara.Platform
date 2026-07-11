using System.Collections.Concurrent;
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
/// connects to each mailbox, fetches messages whose UID is greater than the last one it processed
/// (per-mailbox last-UID tracking → UID-based idempotent dedup, so an already-seen reply is never
/// double-captured), parses each into a MailKit <see cref="MimeKit.MimeMessage"/>, and hands it to
/// <see cref="CsatReplyMailHandler"/> for token verification, rating extraction, and forwarding.
/// </summary>
public sealed partial class ImapInboundPoller : BackgroundService
{
    private readonly ImapPollerOptions _options;
    private readonly CsatReplyMailHandler _replyHandler;
    private readonly ILogger<ImapInboundPoller> _logger;
    private readonly Func<CancellationToken, Task<IImapClient>>? _clientFactory;

    // Per-mailbox last processed UID (keyed by "{tenantId}:{host}:{folder}"). UInt32 is the IMAP UID.
    private readonly ConcurrentDictionary<string, uint> _lastUid = new(StringComparer.Ordinal);

    public ImapInboundPoller(
        IOptions<ImapPollerOptions> options,
        CsatReplyMailHandler replyHandler,
        ILogger<ImapInboundPoller> logger)
    {
        _options = options.Value;
        _replyHandler = replyHandler;
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
        ILogger<ImapInboundPoller> logger,
        Func<CancellationToken, Task<IImapClient>> clientFactory)
    {
        _options = options.Value;
        _replyHandler = replyHandler;
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
    /// Drains one mailbox: connects, fetches messages with UID above the tracked last-UID, hands
    /// each to the reply handler, and advances the last-UID. Public so a host or an integration
    /// test can trigger a single deterministic drain without waiting on the timer.
    /// </summary>
    public async Task<int> PollMailboxAsync(ImapMailboxOptions mailbox, CancellationToken ct)
    {
        var client = await ConnectAsync(mailbox, ct).ConfigureAwait(false);
        try
        {
            var inbox = client.Inbox
                ?? throw new InvalidOperationException("IMAP client exposed no Inbox folder.");
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            var key = MailboxKey(mailbox);
            var lastUid = _lastUid.GetValueOrDefault(key, 0u);

            // UID-range search from just past the last-seen UID → idempotent dedup across polls.
            var query = SearchQuery.Uids(new UniqueIdRange(new UniqueId(lastUid + 1), UniqueId.MaxValue));
            var uids = await inbox.SearchAsync(query, ct).ConfigureAwait(false);

            var processed = 0;
            var maxUid = lastUid;
            foreach (var uid in uids)
            {
                if (uid.Id <= lastUid)
                    continue; // defensive: never reprocess an already-seen UID

                var message = await inbox.GetMessageAsync(uid, ct).ConfigureAwait(false);
                await _replyHandler.HandleReplyAsync(message, ct).ConfigureAwait(false);
                processed++;
                if (uid.Id > maxUid)
                    maxUid = uid.Id;
            }

            if (maxUid > lastUid)
                _lastUid[key] = maxUid;

            LogPolled(mailbox.TenantId, processed);
            return processed;
        }
        finally
        {
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
            client.Dispose();
        }
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

    private static string MailboxKey(ImapMailboxOptions m) => $"{m.TenantId}:{m.Host}:{m.Folder}";

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP inbound poller disabled (Imap:Enabled=false)")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP mailbox drained: tenant={TenantId} processed={Processed}")]
    private partial void LogPolled(string tenantId, int processed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IMAP poll failed: tenant={TenantId} host={Host}")]
    private partial void LogPollError(string tenantId, string host, Exception exception);
}
