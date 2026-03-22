using System.Text.Json.Serialization;

namespace Asterisk.Platform.Core;

[JsonConverter(typeof(Serialization.TenantIdJsonConverter))]
public readonly record struct TenantId
{
    public string Value { get; }

    public TenantId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static implicit operator string(TenantId id) => id.Value;

    public override string ToString() => Value;
}
