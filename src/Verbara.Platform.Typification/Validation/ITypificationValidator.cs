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
    ValidationResult ValidateSubmission(
        TypificationSchema schema,
        IReadOnlyList<EntityId> selectedNodePath,
        IReadOnlyDictionary<string, string> fieldValues);

    /// <summary>
    /// Evaluates a single show/hide condition against the captured field values
    /// and the codes of the nodes in the selected path.
    /// </summary>
    bool EvaluateCondition(
        ConditionExpr expr,
        IReadOnlyDictionary<string, string> fieldValues,
        IReadOnlySet<string> selectedNodeCodes);
}
