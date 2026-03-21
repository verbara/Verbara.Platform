using System.Text.Json.Serialization;

namespace Asterisk.Platform.Channels.Twitter;

// ── Inbound webhook payload ───────────────────────────────────────────────────

internal sealed class TwitterWebhookPayload
{
    [JsonPropertyName("for_user_id")]
    public string? ForUserId { get; set; }

    [JsonPropertyName("direct_message_events")]
    public TwitterDmEvent[]? DirectMessageEvents { get; set; }

    [JsonPropertyName("users")]
    public Dictionary<string, TwitterUser>? Users { get; set; }
}

internal sealed class TwitterDmEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("created_timestamp")]
    public string? CreatedTimestamp { get; set; }

    [JsonPropertyName("message_create")]
    public TwitterDmMessageCreate? MessageCreate { get; set; }
}

internal sealed class TwitterDmMessageCreate
{
    [JsonPropertyName("target")]
    public TwitterDmTarget? Target { get; set; }

    [JsonPropertyName("sender_id")]
    public string? SenderId { get; set; }

    [JsonPropertyName("message_data")]
    public TwitterDmMessageData? MessageData { get; set; }
}

internal sealed class TwitterDmTarget
{
    [JsonPropertyName("recipient_id")]
    public string? RecipientId { get; set; }
}

internal sealed class TwitterDmMessageData
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("quick_reply_response")]
    public TwitterQuickReplyResponse? QuickReplyResponse { get; set; }
}

internal sealed class TwitterQuickReplyResponse
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("metadata")]
    public string? Metadata { get; set; }
}

internal sealed class TwitterUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("screen_name")]
    public string? ScreenName { get; set; }
}

// ── Outbound DM send request ──────────────────────────────────────────────────

internal sealed class TwitterDmSendRequest
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("attachments")]
    public TwitterDmAttachment[]? Attachments { get; set; }
}

internal sealed class TwitterDmAttachment
{
    [JsonPropertyName("media_id")]
    public required string MediaId { get; set; }
}

// ── Outbound DM send response ─────────────────────────────────────────────────

internal sealed class TwitterDmSendResponse
{
    [JsonPropertyName("dm_conversation_id")]
    public string? DmConversationId { get; set; }

    [JsonPropertyName("dm_event_id")]
    public string? DmEventId { get; set; }
}

// ── CRC challenge ─────────────────────────────────────────────────────────────

internal sealed class TwitterCrcResponse
{
    [JsonPropertyName("response_token")]
    public required string ResponseToken { get; set; }
}

// ── Quick reply (outbound) ────────────────────────────────────────────────────

internal sealed class TwitterQuickReplyRequest
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("options")]
    public required TwitterQuickReplyOption[] Options { get; set; }
}

internal sealed class TwitterQuickReplyOption
{
    [JsonPropertyName("label")]
    public required string Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("metadata")]
    public required string Metadata { get; set; }
}

// ── DM with quick reply request ───────────────────────────────────────────────

internal sealed class TwitterDmWithQuickReplyRequest
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("quick_reply")]
    public TwitterQuickReplyRequest? QuickReply { get; set; }
}
