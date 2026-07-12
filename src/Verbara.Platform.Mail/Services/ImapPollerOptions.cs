namespace Verbara.Platform.Mail.Services;

/// <summary>
/// Configuration for the <see cref="ImapInboundPoller"/> (csat-runner Phase C). Binds the
/// <c>Imap</c> configuration section. Each configured <see cref="ImapMailboxOptions"/> is a
/// per-tenant <c>csat@…</c> mailbox the poller drains on a fixed interval, tracking the last
/// processed UID per mailbox so an already-seen message is never double-captured (UID-based
/// idempotent dedup).
/// </summary>
public sealed class ImapPollerOptions
{
    /// <summary>Whether the poller is enabled. When false the hosted service is a no-op.</summary>
    public bool Enabled { get; set; }

    /// <summary>Poll interval between mailbox drains (spec target ~30s).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The token TTL used to verify inbound reply tokens (spec: 7 days).</summary>
    public TimeSpan TokenTtl { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// HMAC-SHA256 signing secret for the CSAT reply token, shared with Pro's dispatcher. Required
    /// when <see cref="Enabled"/> is true; the reused <c>HmacCsatReplyTokenSigner</c> throws when empty.
    /// </summary>
    public string TokenSigningSecret { get; set; } = string.Empty;

    /// <summary>When true, a captured reply triggers a per-tenant auto-reply acknowledgement email.</summary>
    public bool AutoReplyEnabled { get; set; }

    /// <summary>The per-tenant <c>csat@…</c> mailboxes the poller drains.</summary>
    public IList<ImapMailboxOptions> Mailboxes { get; } = [];
}

/// <summary>A single per-tenant IMAP endpoint the poller drains.</summary>
public sealed class ImapMailboxOptions
{
    /// <summary>The tenant this mailbox captures CSAT replies for.</summary>
    public required string TenantId { get; set; }

    /// <summary>IMAP host.</summary>
    public required string Host { get; set; }

    /// <summary>IMAP port (993 IMAPS by default).</summary>
    public int Port { get; set; } = 993;

    /// <summary>Whether to connect over TLS (IMAPS). MailHog test brokers set this false.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>Mailbox login username (typically the <c>csat@…</c> address).</summary>
    public required string Username { get; set; }

    /// <summary>Mailbox login password / app-password.</summary>
    public required string Password { get; set; }

    /// <summary>The folder to poll (defaults to INBOX).</summary>
    public string Folder { get; set; } = "INBOX";
}
