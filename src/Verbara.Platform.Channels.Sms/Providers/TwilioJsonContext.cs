using System.Text.Json.Serialization;

namespace Verbara.Platform.Channels.Sms.Providers;

[JsonSerializable(typeof(TwilioMessageResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class TwilioJsonContext : JsonSerializerContext;

public sealed class TwilioMessageResponse
{
    [JsonPropertyName("sid")]
    public string? Sid { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; init; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }
}
