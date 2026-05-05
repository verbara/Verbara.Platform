using System.Text.Json.Serialization;

namespace Verbara.Platform.Channels.Telegram;

// ── Inbound webhook update ────────────────────────────────────────────────────

internal sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; set; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; set; }
}

internal sealed class TelegramMessage
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    [JsonPropertyName("chat")]
    public TelegramChat? Chat { get; set; }

    [JsonPropertyName("from")]
    public TelegramUser? From { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("photo")]
    public TelegramPhotoSize[]? Photo { get; set; }

    [JsonPropertyName("document")]
    public TelegramDocument? Document { get; set; }

    [JsonPropertyName("audio")]
    public TelegramAudio? Audio { get; set; }

    [JsonPropertyName("video")]
    public TelegramVideo? Video { get; set; }

    [JsonPropertyName("location")]
    public TelegramLocation? Location { get; set; }

    [JsonPropertyName("contact")]
    public TelegramContact? Contact { get; set; }

    [JsonPropertyName("date")]
    public long Date { get; set; }
}

internal sealed class TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

internal sealed class TelegramUser
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

internal sealed class TelegramPhotoSize
{
    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; set; }
}

internal sealed class TelegramDocument
{
    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; set; }
}

internal sealed class TelegramAudio
{
    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; set; }
}

internal sealed class TelegramVideo
{
    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; set; }
}

internal sealed class TelegramLocation
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
}

internal sealed class TelegramContact
{
    [JsonPropertyName("phone_number")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }
}

// ── Outbound send requests ────────────────────────────────────────────────────

internal sealed class TelegramSendMessageRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("parse_mode")]
    public string? ParseMode { get; set; }

    [JsonPropertyName("reply_markup")]
    public TelegramInlineKeyboardMarkup? ReplyMarkup { get; set; }
}

internal sealed class TelegramSendPhotoRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("photo")]
    public required string Photo { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }
}

internal sealed class TelegramSendAudioRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("audio")]
    public required string Audio { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }
}

internal sealed class TelegramSendVideoRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("video")]
    public required string Video { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }
}

internal sealed class TelegramSendDocumentRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("document")]
    public required string Document { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }
}

internal sealed class TelegramSendLocationRequest
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
}

// ── Inline keyboard ───────────────────────────────────────────────────────────

internal sealed class TelegramInlineKeyboardMarkup
{
    [JsonPropertyName("inline_keyboard")]
    public required TelegramInlineKeyboardButton[][] InlineKeyboard { get; set; }
}

internal sealed class TelegramInlineKeyboardButton
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("callback_data")]
    public string? CallbackData { get; set; }
}

// ── API response ──────────────────────────────────────────────────────────────

internal sealed class TelegramApiResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    public TelegramSentMessage? Result { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; set; }
}

internal sealed class TelegramSentMessage
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }
}
