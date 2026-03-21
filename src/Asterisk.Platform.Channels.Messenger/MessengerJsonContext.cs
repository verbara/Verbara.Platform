using System.Text.Json.Serialization;
using Asterisk.Platform.Channels.Messenger.Meta;

namespace Asterisk.Platform.Channels.Messenger;

[JsonSerializable(typeof(MessengerWebhookPayload))]
[JsonSerializable(typeof(MessengerSendRequest))]
[JsonSerializable(typeof(MessengerSendResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class MessengerJsonContext : JsonSerializerContext
{
}
