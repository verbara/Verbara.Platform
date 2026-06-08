namespace Verbara.Platform.Typification.Validation;

/// <summary>A single field-scoped validation failure.</summary>
public sealed record ValidationError(string Field, string Message);

/// <summary>
/// The outcome of a schema/submission validation pass. Collects ALL errors
/// (validators never short-circuit) so the client can surface them together.
/// </summary>
public sealed record ValidationResult(IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Success { get; } = new([]);
}
