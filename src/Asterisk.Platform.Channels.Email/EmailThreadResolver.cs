using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Channels.Email;

/// <summary>
/// Resolves the conversation ID for an inbound email using RFC threading headers,
/// then falls back to subject-based matching.
/// </summary>
public sealed class EmailThreadResolver
{
    private readonly IMessageStore _messageStore;
    private readonly ILogger<EmailThreadResolver> _logger;

    public EmailThreadResolver(IMessageStore messageStore, ILogger<EmailThreadResolver> logger)
    {
        _messageStore = messageStore;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to find an existing conversation for the parsed email.
    /// Returns the <see cref="EntityId"/> of the matching conversation,
    /// or <c>null</c> if no match was found (caller should create a new conversation).
    /// </summary>
    public async Task<EntityId?> ResolveAsync(
        TenantId tenantId,
        ParsedEmail email,
        CancellationToken ct)
    {
        // 1. Try In-Reply-To header — exact message ID match
        if (email.InReplyTo is not null)
        {
            var message = await _messageStore
                .FindByExternalIdAsync(tenantId, email.InReplyTo, ct)
                .ConfigureAwait(false);

            if (message is not null)
            {
                Log.ThreadedByInReplyTo(_logger, email.InReplyTo, message.ConversationId.Value);
                return message.ConversationId;
            }
        }

        // 2. Try References header — check each referenced message ID
        if (email.References is not null)
        {
            foreach (var refId in ParseReferences(email.References))
            {
                var message = await _messageStore
                    .FindByExternalIdAsync(tenantId, refId, ct)
                    .ConfigureAwait(false);

                if (message is not null)
                {
                    Log.ThreadedByReferences(_logger, refId, message.ConversationId.Value);
                    return message.ConversationId;
                }
            }
        }

        // 3. Subject fallback — strip "Re:" prefix and look for prior messages
        var normalizedSubject = NormalizeSubject(email.Subject);
        if (!string.IsNullOrWhiteSpace(normalizedSubject))
        {
            var message = await _messageStore
                .FindByExternalIdAsync(tenantId, $"subject:{normalizedSubject}", ct)
                .ConfigureAwait(false);

            if (message is not null)
            {
                Log.ThreadedBySubject(_logger, normalizedSubject, message.ConversationId.Value);
                return message.ConversationId;
            }
        }

        Log.NoThreadFound(_logger, email.MessageId);
        return null;
    }

    internal static string NormalizeSubject(string subject)
    {
        var s = subject.Trim();
        // Strip Re:, Fwd:, FW: prefixes (case-insensitive, potentially repeated)
        while (true)
        {
            var stripped = s.TrimStart();
            if (stripped.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
                s = stripped[3..].TrimStart();
            else if (stripped.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase))
                s = stripped[4..].TrimStart();
            else if (stripped.StartsWith("FW:", StringComparison.OrdinalIgnoreCase))
                s = stripped[3..].TrimStart();
            else
                break;
        }

        return s.ToUpperInvariant();
    }

    internal static IEnumerable<string> ParseReferences(string references)
    {
        // References header is a space-separated list of Message-IDs
        foreach (var part in references.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var id = part.Trim();
            if (id.Length > 0)
                yield return id;
        }
    }
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Email threaded via In-Reply-To '{InReplyTo}' → conversation '{ConversationId}'")]
    internal static partial void ThreadedByInReplyTo(ILogger logger, string inReplyTo, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Email threaded via References '{RefId}' → conversation '{ConversationId}'")]
    internal static partial void ThreadedByReferences(ILogger logger, string refId, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Email threaded via subject '{Subject}' → conversation '{ConversationId}'")]
    internal static partial void ThreadedBySubject(ILogger logger, string subject, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "No existing thread found for message '{MessageId}' — new conversation required")]
    internal static partial void NoThreadFound(ILogger logger, string messageId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Sending email to '{To}' with subject '{Subject}'")]
    internal static partial void SendingEmail(ILogger logger, string to, string subject);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SMTP error sending email to '{To}'")]
    internal static partial void SmtpError(ILogger logger, Exception exception, string to);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Email sent successfully to '{To}', Message-ID: '{MessageId}'")]
    internal static partial void EmailSent(ILogger logger, string to, string messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Attachment '{FileName}' exceeds max size of {MaxMb} MB — skipping")]
    internal static partial void AttachmentTooLarge(ILogger logger, string fileName, int maxMb);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to parse inbound email")]
    internal static partial void ParseFailed(ILogger logger, Exception exception);
}
