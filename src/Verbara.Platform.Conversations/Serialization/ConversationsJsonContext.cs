using System.Text.Json.Serialization;

namespace Verbara.Platform.Conversations.Serialization;

[JsonSerializable(typeof(MessageBlock))]
[JsonSerializable(typeof(TextBlock))]
[JsonSerializable(typeof(ImageBlock))]
[JsonSerializable(typeof(AudioBlock))]
[JsonSerializable(typeof(VideoBlock))]
[JsonSerializable(typeof(FileBlock))]
[JsonSerializable(typeof(LocationBlock))]
[JsonSerializable(typeof(InteractiveBlock))]
[JsonSerializable(typeof(QuickReply))]
[JsonSerializable(typeof(MessageEnvelope))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(MessageDirection))]
[JsonSerializable(typeof(MessageDeliveryStatus))]
[JsonSerializable(typeof(MessageBlockType))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ConversationsJsonContext : JsonSerializerContext;
