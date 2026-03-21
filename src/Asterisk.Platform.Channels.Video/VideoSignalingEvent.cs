using System.Text.Json.Serialization;

namespace Asterisk.Platform.Channels.Video;

internal sealed class VideoSignalingEvent
{
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("participant_id")]
    public string? ParticipantId { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}
