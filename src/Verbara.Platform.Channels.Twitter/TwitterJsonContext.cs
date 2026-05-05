using System.Text.Json.Serialization;

namespace Verbara.Platform.Channels.Twitter;

[JsonSerializable(typeof(TwitterWebhookPayload))]
[JsonSerializable(typeof(TwitterDmSendRequest))]
[JsonSerializable(typeof(TwitterDmSendResponse))]
[JsonSerializable(typeof(TwitterCrcResponse))]
[JsonSerializable(typeof(TwitterQuickReplyRequest))]
[JsonSerializable(typeof(TwitterDmWithQuickReplyRequest))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TwitterJsonContext : JsonSerializerContext
{
}
