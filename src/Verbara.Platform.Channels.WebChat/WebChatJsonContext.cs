using System.Text.Json.Serialization;

namespace Verbara.Platform.Channels.WebChat;

[JsonSerializable(typeof(WebChatWsMessage))]
[JsonSerializable(typeof(WebChatClientMessage))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class WebChatJsonContext : JsonSerializerContext;
