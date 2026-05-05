using System.Text.Json.Serialization;

namespace Verbara.Platform.Channels.Video;

[JsonSerializable(typeof(VideoSignalingEvent))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class VideoJsonContext : JsonSerializerContext
{
}
