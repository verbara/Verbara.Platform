using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Channels.Email;

/// <summary>
/// Handles inbound email webhooks (raw MIME bytes delivered by an MDA or inbound-email service).
/// Parses the email, resolves conversation threading, and returns an <see cref="InboundMessage"/>.
/// </summary>
public sealed class EmailWebhookHandler : IWebhookHandler
{
    private readonly IEmailParser _parser;
    private readonly EmailThreadResolver _threadResolver;
    private readonly ILogger<EmailWebhookHandler> _logger;

    public ChannelType Channel => ChannelType.Email;

    public EmailWebhookHandler(
        IEmailParser parser,
        EmailThreadResolver threadResolver,
        ILogger<EmailWebhookHandler> logger)
    {
        _parser = parser;
        _threadResolver = threadResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WebhookResult> HandleAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        TenantId tenantId,
        CancellationToken ct)
    {
        ParsedEmail parsed;
        try
        {
            parsed = _parser.Parse(body);
        }
        catch (Exception ex)
        {
            Log.ParseFailed(_logger, ex);
            return Ignored();
        }

        if (string.IsNullOrWhiteSpace(parsed.From))
            return Ignored();

        // Resolve or create conversation thread
        var conversationId = await _threadResolver
            .ResolveAsync(tenantId, parsed, ct)
            .ConfigureAwait(false);

        var blocks = BuildBlocks(parsed);
        var envelope = new MessageEnvelope(blocks);
        var from = new ChannelAddress(ChannelType.Email, parsed.From);

        var inbound = new InboundMessage(from, envelope, parsed.MessageId, parsed.ReceivedAt);
        return new WebhookResult(WebhookResultType.NewMessage, inbound, null);
    }

    private static List<MessageBlock> BuildBlocks(ParsedEmail parsed)
    {
        var blocks = new List<MessageBlock>();

        if (!string.IsNullOrWhiteSpace(parsed.TextBody))
            blocks.Add(new TextBlock(parsed.TextBody));
        else if (!string.IsNullOrWhiteSpace(parsed.HtmlBody))
            blocks.Add(new TextBlock(StripHtml(parsed.HtmlBody)));

        foreach (var att in parsed.Attachments)
        {
            blocks.Add(new FileBlock(
                $"email-attachment:{att.ContentId ?? att.FileName}",
                att.FileName,
                att.ContentType,
                att.SizeBytes));
        }

        return blocks;
    }

    private static string StripHtml(string html)
    {
        var sb = new System.Text.StringBuilder(html.Length);
        var inTag = false;
        foreach (var c in html)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    private static WebhookResult Ignored() =>
        new(WebhookResultType.Ignored, null, null);
}
