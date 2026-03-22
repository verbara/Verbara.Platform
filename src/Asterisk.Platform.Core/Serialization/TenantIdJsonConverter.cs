using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asterisk.Platform.Core.Serialization;

/// <summary>
/// Serializes <see cref="TenantId"/> as a plain JSON string instead of an object.
/// </summary>
public sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    /// <inheritdoc />
    public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return new TenantId(value ?? string.Empty);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
