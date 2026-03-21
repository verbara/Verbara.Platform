using System.Text.Json.Serialization;

namespace Asterisk.Platform.Core;

[JsonSerializable(typeof(ChannelAddress))]
[JsonSerializable(typeof(ChannelType))]
[JsonSerializable(typeof(TenantId))]
[JsonSerializable(typeof(EntityId))]
[JsonSerializable(typeof(MessagePriority))]
[JsonSerializable(typeof(DateRange))]
[JsonSerializable(typeof(PagedResult<string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class PlatformCoreJsonContext : JsonSerializerContext;
