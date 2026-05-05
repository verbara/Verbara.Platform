using System.Text.Json.Serialization;

namespace Verbara.Platform.Core;

[JsonConverter(typeof(Serialization.EntityIdJsonConverter))]
public readonly record struct EntityId
{
    public string Value { get; }

    private EntityId(string value)
    {
        Value = value;
    }

    public static EntityId New() => new(Guid.NewGuid().ToString("N"));

    public static EntityId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new EntityId(value);
    }

    public static implicit operator string(EntityId id) => id.Value;

    public override string ToString() => Value;
}
