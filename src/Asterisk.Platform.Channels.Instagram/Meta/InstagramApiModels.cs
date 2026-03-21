using System.Text.Json.Serialization;

namespace Asterisk.Platform.Channels.Instagram.Meta;

// ── Inbound webhook payload ───────────────────────────────────────────────────

internal sealed class InstagramWebhookPayload
{
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("entry")]
    public InstagramEntry[]? Entry { get; set; }
}

internal sealed class InstagramEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    // DM events arrive in entry[].messaging[]
    [JsonPropertyName("messaging")]
    public InstagramMessagingEvent[]? Messaging { get; set; }

    // Comment/mention events arrive in entry[].changes[]
    [JsonPropertyName("changes")]
    public InstagramChange[]? Changes { get; set; }
}

internal sealed class InstagramMessagingEvent
{
    [JsonPropertyName("sender")]
    public InstagramSender? Sender { get; set; }

    [JsonPropertyName("recipient")]
    public InstagramRecipient? Recipient { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("message")]
    public InstagramInboundMessage? Message { get; set; }

    [JsonPropertyName("delivery")]
    public InstagramDelivery? Delivery { get; set; }
}

internal sealed class InstagramSender
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class InstagramRecipient
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class InstagramInboundMessage
{
    [JsonPropertyName("mid")]
    public string? Mid { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("attachments")]
    public InstagramAttachment[]? Attachments { get; set; }

    [JsonPropertyName("quick_reply")]
    public InstagramQuickReplyPayload? QuickReply { get; set; }
}

internal sealed class InstagramAttachment
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("payload")]
    public InstagramAttachmentPayload? Payload { get; set; }
}

internal sealed class InstagramAttachmentPayload
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal sealed class InstagramQuickReplyPayload
{
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }
}

internal sealed class InstagramDelivery
{
    [JsonPropertyName("mids")]
    public string[]? Mids { get; set; }

    [JsonPropertyName("watermark")]
    public long Watermark { get; set; }
}

// Comment/mention changes — we receive but ignore them
internal sealed class InstagramChange
{
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("value")]
    public InstagramChangeValue? Value { get; set; }
}

internal sealed class InstagramChangeValue
{
    [JsonPropertyName("from")]
    public InstagramSender? From { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

// ── Outbound send request ─────────────────────────────────────────────────────

internal sealed class InstagramSendRequest
{
    [JsonPropertyName("recipient")]
    public required InstagramSendRecipient Recipient { get; set; }

    [JsonPropertyName("message")]
    public required InstagramSendMessage Message { get; set; }

    [JsonPropertyName("messaging_type")]
    public string MessagingType { get; set; } = "RESPONSE";
}

internal sealed class InstagramSendRecipient
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }
}

internal sealed class InstagramSendMessage
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("attachment")]
    public InstagramSendAttachment? Attachment { get; set; }

    [JsonPropertyName("quick_replies")]
    public InstagramSendQuickReply[]? QuickReplies { get; set; }
}

internal sealed class InstagramSendAttachment
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("payload")]
    public required InstagramSendAttachmentPayload Payload { get; set; }
}

internal sealed class InstagramSendAttachmentPayload
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("is_reusable")]
    public bool IsReusable { get; set; }
}

internal sealed class InstagramSendQuickReply
{
    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "text";

    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("payload")]
    public required string Payload { get; set; }
}

// ── Send response ─────────────────────────────────────────────────────────────

internal sealed class InstagramSendResponse
{
    [JsonPropertyName("recipient_id")]
    public string? RecipientId { get; set; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("error")]
    public InstagramApiError? Error { get; set; }
}

internal sealed class InstagramApiError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("fbtrace_id")]
    public string? FbTraceId { get; set; }
}
