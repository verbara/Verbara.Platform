using System.Text.Json.Serialization;

namespace Verbara.Platform.Channels.WhatsApp.Meta;

// ── Inbound webhook payload ───────────────────────────────────────────────────

internal sealed class MetaWebhookPayload
{
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("entry")]
    public MetaEntry[]? Entry { get; set; }
}

internal sealed class MetaEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("changes")]
    public MetaChange[]? Changes { get; set; }
}

internal sealed class MetaChange
{
    [JsonPropertyName("value")]
    public MetaChangeValue? Value { get; set; }

    [JsonPropertyName("field")]
    public string? Field { get; set; }
}

internal sealed class MetaChangeValue
{
    [JsonPropertyName("messaging_product")]
    public string? MessagingProduct { get; set; }

    [JsonPropertyName("metadata")]
    public MetaMetadata? Metadata { get; set; }

    [JsonPropertyName("contacts")]
    public MetaContact[]? Contacts { get; set; }

    [JsonPropertyName("messages")]
    public MetaInboundMessage[]? Messages { get; set; }

    [JsonPropertyName("statuses")]
    public MetaStatusUpdate[]? Statuses { get; set; }
}

internal sealed class MetaMetadata
{
    [JsonPropertyName("display_phone_number")]
    public string? DisplayPhoneNumber { get; set; }

    [JsonPropertyName("phone_number_id")]
    public string? PhoneNumberId { get; set; }
}

internal sealed class MetaContact
{
    [JsonPropertyName("profile")]
    public MetaProfile? Profile { get; set; }

    [JsonPropertyName("wa_id")]
    public string? WaId { get; set; }
}

internal sealed class MetaProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class MetaInboundMessage
{
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public MetaTextContent? Text { get; set; }

    [JsonPropertyName("image")]
    public MetaMediaContent? Image { get; set; }

    [JsonPropertyName("audio")]
    public MetaMediaContent? Audio { get; set; }

    [JsonPropertyName("video")]
    public MetaMediaContent? Video { get; set; }

    [JsonPropertyName("document")]
    public MetaDocumentContent? Document { get; set; }

    [JsonPropertyName("location")]
    public MetaLocationContent? Location { get; set; }

    [JsonPropertyName("interactive")]
    public MetaInboundInteractive? Interactive { get; set; }
}

internal sealed class MetaTextContent
{
    [JsonPropertyName("body")]
    public string? Body { get; set; }
}

internal sealed class MetaMediaContent
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

internal sealed class MetaDocumentContent
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }
}

internal sealed class MetaLocationContent
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

internal sealed class MetaInboundInteractive
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("button_reply")]
    public MetaButtonReply? ButtonReply { get; set; }

    [JsonPropertyName("list_reply")]
    public MetaListReply? ListReply { get; set; }
}

internal sealed class MetaButtonReply
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

internal sealed class MetaListReply
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

internal sealed class MetaStatusUpdate
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("recipient_id")]
    public string? RecipientId { get; set; }
}

// ── Outbound send request ─────────────────────────────────────────────────────

internal sealed class MetaSendRequest
{
    [JsonPropertyName("messaging_product")]
    public string MessagingProduct { get; set; } = "whatsapp";

    [JsonPropertyName("recipient_type")]
    public string RecipientType { get; set; } = "individual";

    [JsonPropertyName("to")]
    public required string To { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("text")]
    public MetaSendText? Text { get; set; }

    [JsonPropertyName("image")]
    public MetaSendMedia? Image { get; set; }

    [JsonPropertyName("audio")]
    public MetaSendMedia? Audio { get; set; }

    [JsonPropertyName("video")]
    public MetaSendMedia? Video { get; set; }

    [JsonPropertyName("document")]
    public MetaSendDocument? Document { get; set; }

    [JsonPropertyName("location")]
    public MetaSendLocation? Location { get; set; }

    [JsonPropertyName("interactive")]
    public MetaSendInteractive? Interactive { get; set; }

    [JsonPropertyName("template")]
    public MetaSendTemplate? Template { get; set; }
}

internal sealed class MetaSendText
{
    [JsonPropertyName("body")]
    public required string Body { get; set; }

    [JsonPropertyName("preview_url")]
    public bool PreviewUrl { get; set; }
}

internal sealed class MetaSendMedia
{
    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }
}

internal sealed class MetaSendDocument
{
    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }
}

internal sealed class MetaSendLocation
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

internal sealed class MetaSendInteractive
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("body")]
    public required MetaInteractiveBody Body { get; set; }

    [JsonPropertyName("action")]
    public required MetaInteractiveAction Action { get; set; }
}

internal sealed class MetaInteractiveBody
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

internal sealed class MetaInteractiveAction
{
    [JsonPropertyName("buttons")]
    public MetaInteractiveButton[]? Buttons { get; set; }
}

internal sealed class MetaInteractiveButton
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "reply";

    [JsonPropertyName("reply")]
    public required MetaButtonReplyContent Reply { get; set; }
}

internal sealed class MetaButtonReplyContent
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("title")]
    public required string Title { get; set; }
}

internal sealed class MetaSendTemplate
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("language")]
    public MetaTemplateLanguage Language { get; set; } = new();
}

internal sealed class MetaTemplateLanguage
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "en_US";
}

// ── Send response ─────────────────────────────────────────────────────────────

internal sealed class MetaSendResponse
{
    [JsonPropertyName("messaging_product")]
    public string? MessagingProduct { get; set; }

    [JsonPropertyName("contacts")]
    public MetaResponseContact[]? Contacts { get; set; }

    [JsonPropertyName("messages")]
    public MetaResponseMessage[]? Messages { get; set; }

    [JsonPropertyName("error")]
    public MetaApiError? Error { get; set; }
}

internal sealed class MetaResponseContact
{
    [JsonPropertyName("input")]
    public string? Input { get; set; }

    [JsonPropertyName("wa_id")]
    public string? WaId { get; set; }
}

internal sealed class MetaResponseMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class MetaApiError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("error_subcode")]
    public int? ErrorSubcode { get; set; }

    [JsonPropertyName("fbtrace_id")]
    public string? FbTraceId { get; set; }
}
