using System.Text.Json.Serialization;
using Asterisk.Platform.Channels.Instagram.Meta;

namespace Asterisk.Platform.Channels.Instagram;

[JsonSerializable(typeof(InstagramWebhookPayload))]
[JsonSerializable(typeof(InstagramSendRequest))]
[JsonSerializable(typeof(InstagramSendResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class InstagramJsonContext : JsonSerializerContext
{
}
