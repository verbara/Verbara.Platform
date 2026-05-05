using System.Text.Json.Serialization;
using Verbara.Platform.Channels.Instagram.Meta;

namespace Verbara.Platform.Channels.Instagram;

[JsonSerializable(typeof(InstagramWebhookPayload))]
[JsonSerializable(typeof(InstagramSendRequest))]
[JsonSerializable(typeof(InstagramSendResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class InstagramJsonContext : JsonSerializerContext
{
}
