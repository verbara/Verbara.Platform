using System.Text.Json.Serialization;

namespace Verbara.Platform.Channels.Messenger.Meta;

// ── Inbound webhook payload ───────────────────────────────────────────────────

internal sealed class MessengerWebhookPayload
{
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("entry")]
    public MessengerEntry[]? Entry { get; set; }
}

internal sealed class MessengerEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("messaging")]
    public MessengerEvent[]? Messaging { get; set; }
}

internal sealed class MessengerEvent
{
    [JsonPropertyName("sender")]
    public MessengerSender? Sender { get; set; }

    [JsonPropertyName("recipient")]
    public MessengerRecipient? Recipient { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("message")]
    public MessengerInboundMessage? Message { get; set; }

    [JsonPropertyName("delivery")]
    public MessengerDelivery? Delivery { get; set; }

    [JsonPropertyName("read")]
    public MessengerRead? Read { get; set; }
}

internal sealed class MessengerSender
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class MessengerRecipient
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class MessengerInboundMessage
{
    [JsonPropertyName("mid")]
    public string? Mid { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("attachments")]
    public MessengerAttachment[]? Attachments { get; set; }

    [JsonPropertyName("quick_reply")]
    public MessengerQuickReplyPayload? QuickReply { get; set; }
}

internal sealed class MessengerAttachment
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("payload")]
    public MessengerAttachmentPayload? Payload { get; set; }
}

internal sealed class MessengerAttachmentPayload
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("sticker_id")]
    public long? StickerId { get; set; }
}

internal sealed class MessengerQuickReplyPayload
{
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }
}

internal sealed class MessengerDelivery
{
    [JsonPropertyName("mids")]
    public string[]? Mids { get; set; }

    [JsonPropertyName("watermark")]
    public long Watermark { get; set; }
}

internal sealed class MessengerRead
{
    [JsonPropertyName("watermark")]
    public long Watermark { get; set; }
}

// ── Outbound send request ─────────────────────────────────────────────────────

internal sealed class MessengerSendRequest
{
    [JsonPropertyName("recipient")]
    public required MessengerSendRecipient Recipient { get; set; }

    [JsonPropertyName("message")]
    public required MessengerSendMessage Message { get; set; }

    [JsonPropertyName("messaging_type")]
    public string MessagingType { get; set; } = "RESPONSE";
}

internal sealed class MessengerSendRecipient
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }
}

internal sealed class MessengerSendMessage
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("attachment")]
    public MessengerSendAttachment? Attachment { get; set; }

    [JsonPropertyName("quick_replies")]
    public MessengerSendQuickReply[]? QuickReplies { get; set; }
}

internal sealed class MessengerSendAttachment
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("payload")]
    public required MessengerSendAttachmentPayload Payload { get; set; }
}

internal sealed class MessengerSendAttachmentPayload
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("is_reusable")]
    public bool IsReusable { get; set; }

    [JsonPropertyName("template_type")]
    public string? TemplateType { get; set; }

    [JsonPropertyName("elements")]
    public MessengerTemplateElement[]? Elements { get; set; }
}

internal sealed class MessengerTemplateElement
{
    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("buttons")]
    public MessengerButton[]? Buttons { get; set; }
}

internal sealed class MessengerButton
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal sealed class MessengerSendQuickReply
{
    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "text";

    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("payload")]
    public required string Payload { get; set; }
}

// ── Send response ─────────────────────────────────────────────────────────────

internal sealed class MessengerSendResponse
{
    [JsonPropertyName("recipient_id")]
    public string? RecipientId { get; set; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("error")]
    public MessengerApiError? Error { get; set; }
}

internal sealed class MessengerApiError
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
