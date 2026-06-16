using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Validation;

/// <summary>
/// Server-authoritative validation for typification schemas and submissions.
/// Pure (no I/O, no DI deps): the client mirrors these rules for UX only — the
/// server never trusts the client and recomputes everything from the schema.
/// </summary>
public interface ITypificationValidator
{
    /// <summary>
    /// Structural validation run before a schema may be published: depth bounds,
    /// parent-graph integrity, code/key uniqueness, leaf consistency and
    /// condition-reference integrity. Collects ALL errors.
    /// </summary>
    ValidationResult ValidateForPublish(TypificationSchema schema);

    /// <summary>
    /// Validates an agent submission against a (published) schema: the selected
    /// node path must be a valid root→leaf chain, and every ACTIVE field must
    /// satisfy required/typed/format constraints. Collects ALL errors.
    /// </summary>
    /// <param name="schema">The published schema the submission is validated against.</param>
    /// <param name="selectedNodePath">The selected root→leaf node-id chain.</param>
    /// <param name="fieldValues">The captured field values keyed by field <c>Key</c>.</param>
    /// <param name="source">
    /// Server-derived provenance of the submission (D3). When
    /// <see cref="SubmissionSource.AutoAi"/>, free-text fields
    /// (<c>Text</c>/<c>Textarea</c>/<c>Lookup</c>) are length-capped even when the field
    /// declares no <c>MaxLength</c> — a defense against the model emitting a huge blob.
    /// Defaults to <see cref="SubmissionSource.Manual"/>, preserving prior behavior for
    /// existing callers.
    /// </param>
    ValidationResult ValidateSubmission(
        TypificationSchema schema,
        IReadOnlyList<EntityId> selectedNodePath,
        IReadOnlyDictionary<string, string> fieldValues,
        SubmissionSource source = SubmissionSource.Manual);

    /// <summary>
    /// Evaluates a single show/hide condition against the captured field values
    /// and the codes of the nodes in the selected path.
    /// </summary>
    bool EvaluateCondition(
        ConditionExpr expr,
        IReadOnlyDictionary<string, string> fieldValues,
        IReadOnlySet<string> selectedNodeCodes);
}
