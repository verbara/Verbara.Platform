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

    /// <summary>
    /// Non-throwing input-shape validator. Mirrors the contract enforced by
    /// <see cref="From(string)"/> (non-null, non-whitespace) so callers can
    /// reject malformed ids before reaching the store layer. Added for the
    /// <c>BILL-002</c> remediation (PREPUB-2026-05-09): user-supplied
    /// <c>invoiceId</c> must be validated before <see cref="From(string)"/> in
    /// path-bound endpoints to avoid leaking <see cref="ArgumentException"/>
    /// stack traces on garbage input.
    /// </summary>
    public static bool IsValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value);

    public static implicit operator string(EntityId id) => id.Value;

    public override string ToString() => Value;
}
