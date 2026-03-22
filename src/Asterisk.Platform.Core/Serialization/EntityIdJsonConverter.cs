using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asterisk.Platform.Core.Serialization;

public sealed class EntityIdJsonConverter : JsonConverter<EntityId>
{
    public override EntityId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return EntityId.From(value ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, EntityId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
