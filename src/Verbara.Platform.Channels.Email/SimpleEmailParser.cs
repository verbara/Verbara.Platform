using System.Text;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Email;

/// <summary>
/// Default MIME email parser. Uses line-by-line parsing to remain AOT-compatible.
/// Handles multipart/mixed and multipart/alternative. Extracts RFC threading headers.
/// </summary>
public sealed class SimpleEmailParser : IEmailParser
{
    /// <inheritdoc />
    public ParsedEmail Parse(ReadOnlyMemory<byte> rawEmail)
    {
        var text = Encoding.UTF8.GetString(rawEmail.Span);
        var lines = text.Split('\n');

        var headers = ParseHeaders(lines, out var bodyStartIndex);

        var messageId = headers.TryGetValue("message-id", out var mid)
            ? NormalizeMessageId(mid)
            : $"<{Guid.NewGuid()}@unknown>";

        var inReplyTo = headers.TryGetValue("in-reply-to", out var irt)
            ? NormalizeMessageId(irt)
            : null;

        var references = headers.TryGetValue("references", out var refs) ? refs.Trim() : null;
        var from = headers.TryGetValue("from", out var fr) ? fr.Trim() : string.Empty;
        var subject = headers.TryGetValue("subject", out var subj) ? subj.Trim() : string.Empty;

        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
        if (headers.TryGetValue("date", out var dateStr))
        {
            if (DateTimeOffset.TryParse(dateStr.Trim(), out var parsed))
                receivedAt = parsed.ToUtcInstant();
        }

        var contentType = headers.TryGetValue("content-type", out var ct) ? ct : "text/plain";
        var body = string.Join("\n", lines.Skip(bodyStartIndex));

        string textBody;
        string? htmlBody = null;
        List<EmailAttachment> attachments = [];

        if (contentType.Contains("multipart", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = ExtractBoundary(contentType);
            if (boundary is not null)
            {
                ParseMultipart(body, boundary, out textBody, out htmlBody, out attachments);
            }
            else
            {
                textBody = body.Trim();
            }
        }
        else if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            htmlBody = DecodeBody(body, headers);
            textBody = StripHtml(htmlBody);
        }
        else
        {
            textBody = DecodeBody(body, headers).Trim();
        }

        return new ParsedEmail(
            messageId,
            inReplyTo,
            references,
            from,
            subject,
            textBody,
            htmlBody,
            attachments,
            receivedAt);
    }

    private static Dictionary<string, string> ParseHeaders(string[] lines, out int bodyStartIndex)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bodyStartIndex = 0;
        string? currentKey = null;
        var currentValue = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (string.IsNullOrEmpty(line))
            {
                // Flush last header
                if (currentKey is not null)
                    headers[currentKey] = currentValue.ToString();
                bodyStartIndex = i + 1;
                break;
            }

            // Folded header (starts with whitespace)
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                currentValue.Append(' ').Append(line.Trim());
                continue;
            }

            // New header line
            if (currentKey is not null)
                headers[currentKey] = currentValue.ToString();

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                currentKey = line[..colon].Trim();
                currentValue.Clear();
                currentValue.Append(line[(colon + 1)..].Trim());
            }
        }

        return headers;
    }

    private static string? ExtractBoundary(string contentType)
    {
        const string marker = "boundary=";
        var idx = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var value = contentType[(idx + marker.Length)..].Trim();
        if (value.StartsWith('"') && value.EndsWith('"'))
            value = value[1..^1];

        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0)
            value = value[..semicolon].Trim();

        return value;
    }

    private static void ParseMultipart(
        string body,
        string boundary,
        out string textBody,
        out string? htmlBody,
        out List<EmailAttachment> attachments)
    {
        textBody = string.Empty;
        htmlBody = null;
        attachments = [];

        var delimiter = "--" + boundary;
        var parts = body.Split(delimiter, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var trimmed = part.TrimStart('\r', '\n');
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
                continue; // final delimiter

            var partLines = trimmed.Split('\n');
            var partHeaders = ParseHeaders(partLines, out var partBodyStart);
            var partBody = string.Join("\n", partLines.Skip(partBodyStart)).Trim();

            var partContentType = partHeaders.TryGetValue("content-type", out var pct)
                ? pct
                : "text/plain";

            var disposition = partHeaders.TryGetValue("content-disposition", out var cd) ? cd : string.Empty;
            var isAttachment = disposition.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);

            if (isAttachment || (!partContentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)
                && !partContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                && !partContentType.Contains("multipart", StringComparison.OrdinalIgnoreCase)))
            {
                var fileName = ExtractParameter(disposition, "filename")
                    ?? ExtractParameter(partContentType, "name")
                    ?? "attachment";
                var contentId = partHeaders.TryGetValue("content-id", out var cid)
                    ? NormalizeMessageId(cid)
                    : null;

                // Estimate size from base64 content
                var sizeBytes = partBody.Length * 3L / 4;
                attachments.Add(new EmailAttachment(
                    fileName,
                    partContentType.Split(';')[0].Trim(),
                    sizeBytes,
                    contentId));
            }
            else if (partContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                htmlBody = DecodeBody(partBody, partHeaders);
            }
            else if (partContentType.Contains("multipart", StringComparison.OrdinalIgnoreCase))
            {
                var nestedBoundary = ExtractBoundary(partContentType);
                if (nestedBoundary is not null)
                {
                    ParseMultipart(partBody, nestedBoundary,
                        out var nestedText, out var nestedHtml, out var nestedAttachments);
                    if (!string.IsNullOrEmpty(nestedText)) textBody = nestedText;
                    htmlBody ??= nestedHtml;
                    attachments.AddRange(nestedAttachments);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(textBody))
                    textBody = DecodeBody(partBody, partHeaders).Trim();
            }
        }
    }

    private static string DecodeBody(string body, Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("content-transfer-encoding", out var encoding))
            return body;

        var enc = encoding.Trim();
        if (enc.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var cleaned = body.Replace("\r", "").Replace("\n", "");
                var bytes = Convert.FromBase64String(cleaned);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                return body;
            }
        }

        if (enc.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase))
            return DecodeQuotedPrintable(body);

        return body;
    }

    private static string DecodeQuotedPrintable(string input)
    {
        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] == '=' && i + 2 < input.Length)
            {
                var hex = input.Substring(i + 1, 2);
                if (hex == "\r\n" || hex == "\n\r" || hex[0] == '\r' || hex[0] == '\n')
                {
                    i += hex[0] == '\r' && i + 2 < input.Length && input[i + 2] == '\n' ? 3 : 2;
                    continue;
                }

                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                {
                    sb.Append((char)code);
                    i += 3;
                    continue;
                }
            }

            sb.Append(input[i]);
            i++;
        }

        return sb.ToString();
    }

    private static string? ExtractParameter(string headerValue, string paramName)
    {
        var marker = paramName + "=";
        var idx = headerValue.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var value = headerValue[(idx + marker.Length)..].Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 0 ? value[1..end] : value[1..];
        }

        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        return semicolon >= 0 ? value[..semicolon].Trim() : value.Trim();
    }

    private static string NormalizeMessageId(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('<'))
            trimmed = "<" + trimmed;
        if (!trimmed.EndsWith('>'))
            trimmed += ">";
        return trimmed;
    }

    private static string StripHtml(string html)
    {
        // Simple tag stripper — adequate for plain-text fallback
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var c in html)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
