using System.Text.Json.Serialization;

namespace Asterisk.Platform.Channels.Telegram;

[JsonSerializable(typeof(TelegramUpdate))]
[JsonSerializable(typeof(TelegramApiResponse))]
[JsonSerializable(typeof(TelegramSendMessageRequest))]
[JsonSerializable(typeof(TelegramSendPhotoRequest))]
[JsonSerializable(typeof(TelegramSendAudioRequest))]
[JsonSerializable(typeof(TelegramSendVideoRequest))]
[JsonSerializable(typeof(TelegramSendDocumentRequest))]
[JsonSerializable(typeof(TelegramSendLocationRequest))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TelegramJsonContext : JsonSerializerContext
{
}
