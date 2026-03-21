using System.Text.Json.Serialization;

namespace Asterisk.Platform.Channels.Rcs;

[JsonSerializable(typeof(RbmWebhookPayload))]
[JsonSerializable(typeof(RbmDeliveryReceipt))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class RcsJsonContext : JsonSerializerContext
{
}
