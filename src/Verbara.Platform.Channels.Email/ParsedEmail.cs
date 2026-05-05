namespace Verbara.Platform.Channels.Email;

/// <summary>A structured email parsed from raw MIME bytes.</summary>
public sealed record ParsedEmail(
    string MessageId,
    string? InReplyTo,
    string? References,
    string From,
    string Subject,
    string TextBody,
    string? HtmlBody,
    IReadOnlyList<EmailAttachment> Attachments,
    DateTimeOffset ReceivedAt);

/// <summary>An email attachment or inline image part.</summary>
public sealed record EmailAttachment(
    string FileName,
    string ContentType,
    long SizeBytes,
    string? ContentId);
