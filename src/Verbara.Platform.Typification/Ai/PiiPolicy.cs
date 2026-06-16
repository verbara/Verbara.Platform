using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// A tenant's allow-list policy for storing personally-identifiable information that the
/// AI may extract into typification field values (D1, P2b). Any <see cref="PiiType"/>
/// NOT in <see cref="AllowStore"/> is masked by <see cref="PiiScreen.Apply"/>; allow-listed
/// types are passed through unchanged.
/// </summary>
/// <remarks>
/// Serialized (Native AOT, reflection-free) by <see cref="PiiPolicyJsonConverter"/> as a
/// JSON object with a single <c>allowStore</c> array of <see cref="PiiType"/> names, so it
/// round-trips inside the typification schema definition JSONB even though
/// <see cref="AllowStore"/> is an interface-typed, <c>required</c>, frozen set that the
/// source generator cannot populate directly.
/// </remarks>
[JsonConverter(typeof(PiiPolicyJsonConverter))]
public sealed class PiiPolicy
{
    /// <summary>
    /// PII types the tenant has explicitly allowed to store unmasked. Empty (the default)
    /// = deny all sensitive types (mask everything detected).
    /// </summary>
    public required IReadOnlySet<PiiType> AllowStore { get; init; }

    /// <summary>The most restrictive policy: every detected PII type is masked.</summary>
    /// <remarks>
    /// Backed by <see cref="FrozenSet{T}.Empty"/> so this shared singleton is genuinely immutable
    /// (cannot be down-cast and mutated) and its <c>Contains</c> stays fast on the screening hot path.
    /// </remarks>
    public static PiiPolicy DenyAll { get; } = new() { AllowStore = FrozenSet<PiiType>.Empty };
}

/// <summary>
/// Reflection-free (Native AOT) JSON converter for <see cref="PiiPolicy"/>: reads/writes a
/// <c>{ "allowStore": ["Email", ...] }</c> object, mapping the array to a
/// <see cref="FrozenSet{T}"/> on read and enumerating <see cref="PiiPolicy.AllowStore"/> on
/// write. <see cref="PiiType"/> members serialize as their names via the enum's own
/// <see cref="JsonStringEnumConverter{TEnum}"/>.
/// </summary>
public sealed class PiiPolicyJsonConverter : JsonConverter<PiiPolicy>
{
    private const string AllowStorePropertyName = "allowStore";

    public override PiiPolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a PiiPolicy object.");

        HashSet<PiiType>? allow = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected a PiiPolicy property name.");

            var name = reader.GetString();
            reader.Read();

            if (string.Equals(name, AllowStorePropertyName, StringComparison.OrdinalIgnoreCase))
            {
                allow = ReadAllowStore(ref reader);
            }
            else
            {
                reader.Skip();
            }
        }

        var set = allow is null || allow.Count == 0
            ? (IReadOnlySet<PiiType>)FrozenSet<PiiType>.Empty
            : allow.ToFrozenSet();

        return new PiiPolicy { AllowStore = set };
    }

    private static HashSet<PiiType> ReadAllowStore(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new HashSet<PiiType>();

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected the allowStore array.");

        var values = new HashSet<PiiType>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            values.Add(ReadPiiType(ref reader));
        }

        return values;
    }

    private static PiiType ReadPiiType(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (Enum.TryParse<PiiType>(text, ignoreCase: true, out var byName))
                return byName;

            throw new JsonException($"Unknown PiiType '{text}'.");
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var ordinal)
            && Enum.IsDefined((PiiType)ordinal))
        {
            return (PiiType)ordinal;
        }

        throw new JsonException("Expected a PiiType name or ordinal.");
    }

    public override void Write(Utf8JsonWriter writer, PiiPolicy value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteStartArray(AllowStorePropertyName);
        foreach (var type in value.AllowStore)
            writer.WriteStringValue(type.ToString());

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
