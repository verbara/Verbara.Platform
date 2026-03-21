namespace Asterisk.Platform.Core;

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
