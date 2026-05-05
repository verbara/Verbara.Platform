using System.Text.Json.Serialization;
using Verbara.Platform.Channels.WhatsApp.Meta;

namespace Verbara.Platform.Channels.WhatsApp;

[JsonSerializable(typeof(MetaWebhookPayload))]
[JsonSerializable(typeof(MetaSendRequest))]
[JsonSerializable(typeof(MetaSendResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class WhatsAppJsonContext : JsonSerializerContext
{
}
