using System.Globalization;
using System.Text.RegularExpressions;
using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Validation;

/// <summary>
/// Pure, reflection-free implementation of <see cref="ITypificationValidator"/>.
/// No I/O, no DI dependencies — safe to register as a singleton.
/// </summary>
public sealed class DefaultTypificationValidator : ITypificationValidator
{
    private const int MinAllowedDepth = 1;
    private const int MaxAllowedDepth = 8;

    /// <summary>
    /// Hard length cap (chars) for AI-sourced free-text values (Text/Textarea/Lookup) that
    /// declare no explicit <see cref="FieldValidation.MaxLength"/> — guards the AI write path
    /// against the model emitting a huge unbounded blob (D3). For Manual submissions only the
    /// configured per-field MaxLength applies (no implicit cap).
    /// </summary>
    private const int AiFreeTextMaxLength = 2000;

    /// <summary>Bounded match time to avoid catastrophic-backtracking (ReDoS).</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public ValidationResult ValidateForPublish(TypificationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var errors = new List<ValidationError>();

        // --- MaxDepth bounds ---
        if (schema.MaxDepth is < MinAllowedDepth or > MaxAllowedDepth)
        {
            errors.Add(new ValidationError(
                "maxDepth",
                $"MaxDepth must be between {MinAllowedDepth} and {MaxAllowedDepth} (was {schema.MaxDepth})."));
        }

        // --- Index nodes by id; detect duplicate node ids defensively ---
        var nodesById = new Dictionary<EntityId, TypificationNode>();
        foreach (var node in schema.Nodes)
        {
            if (!nodesById.TryAdd(node.NodeId, node))
            {
                errors.Add(new ValidationError(
                    "nodes",
                    $"Duplicate node id '{node.NodeId.Value}'."));
            }
        }

        // --- Node Code uniqueness ---
        var seenNodeCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in schema.Nodes)
        {
            if (!seenNodeCodes.Add(node.Code))
            {
                errors.Add(new ValidationError(
                    "nodes",
                    $"Node Code '{node.Code}' is not unique within the schema."));
            }
        }

        // --- Field Key uniqueness ---
        var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            if (!fieldKeys.Add(field.Key))
            {
                errors.Add(new ValidationError(
                    "fields",
                    $"Field Key '{field.Key}' is not unique within the schema."));
            }
        }

        // --- Parent-graph integrity (existence, rooted, acyclic) + depth bound ---
        // children lookup used for leaf-consistency.
        var childCount = new Dictionary<EntityId, int>();
        foreach (var node in schema.Nodes)
        {
            if (node.ParentNodeId is { } parentId)
            {
                if (nodesById.ContainsKey(parentId))
                {
                    childCount[parentId] = childCount.GetValueOrDefault(parentId) + 1;
                }
                else
                {
                    errors.Add(new ValidationError(
                        "nodes",
                        $"Node '{node.Code}' references a missing parent node id '{parentId.Value}'."));
                }
            }
        }

        foreach (var node in schema.Nodes)
        {
            var depth = ComputeDepth(node, nodesById, out var cyclic);
            if (cyclic)
            {
                errors.Add(new ValidationError(
                    "nodes",
                    $"Node '{node.Code}' is part of a parent cycle."));
                continue;
            }

            if (depth > schema.MaxDepth)
            {
                errors.Add(new ValidationError(
                    "nodes",
                    $"Node '{node.Code}' has depth {depth} which exceeds MaxDepth {schema.MaxDepth}."));
            }
        }

        // --- Leaf consistency ---
        foreach (var node in schema.Nodes)
        {
            var hasChildren = childCount.GetValueOrDefault(node.NodeId) > 0;
            var actuallyLeaf = !hasChildren;

            if (node.IsLeaf != actuallyLeaf)
            {
                errors.Add(new ValidationError(
                    "nodes",
                    actuallyLeaf
                        ? $"Node '{node.Code}' has no children so it must be a leaf (IsLeaf=true)."
                        : $"Node '{node.Code}' has children so it must not be a leaf (IsLeaf=false)."));
            }

            if (node.IsLeaf)
            {
                if (node.Leaf is null)
                {
                    errors.Add(new ValidationError(
                        "nodes",
                        $"Leaf node '{node.Code}' must have a non-null Leaf outcome."));
                }
            }
            else
            {
                if (node.Leaf is not null)
                {
                    errors.Add(new ValidationError(
                        "nodes",
                        $"Non-leaf node '{node.Code}' must have a null Leaf outcome."));
                }

                if (!hasChildren)
                {
                    errors.Add(new ValidationError(
                        "nodes",
                        $"Non-leaf node '{node.Code}' must have at least one child."));
                }
            }
        }

        // --- Field VisibleWhen + AttachToNodeId reference integrity ---
        foreach (var field in schema.Fields)
        {
            if (field.AttachToNodeId is { } attachId && !nodesById.ContainsKey(attachId))
            {
                errors.Add(new ValidationError(
                    field.Key,
                    $"Field '{field.Key}' AttachToNodeId references a missing node id '{attachId.Value}'."));
            }

            if (field.VisibleWhen is { } cond)
            {
                switch (cond.RefType)
                {
                    case ConditionRef.Field:
                        if (!fieldKeys.Contains(cond.Ref))
                        {
                            errors.Add(new ValidationError(
                                field.Key,
                                $"Field '{field.Key}' VisibleWhen references an unknown field Key '{cond.Ref}'."));
                        }
                        break;
                    case ConditionRef.NodeSelected:
                        if (!seenNodeCodes.Contains(cond.Ref))
                        {
                            errors.Add(new ValidationError(
                                field.Key,
                                $"Field '{field.Key}' VisibleWhen references an unknown node Code '{cond.Ref}'."));
                        }
                        break;
                    default:
                        errors.Add(new ValidationError(
                            field.Key,
                            $"Field '{field.Key}' VisibleWhen has an unsupported RefType."));
                        break;
                }
            }
        }

        return errors.Count == 0 ? ValidationResult.Success : new ValidationResult(errors);
    }

    public ValidationResult ValidateSubmission(
        TypificationSchema schema,
        IReadOnlyList<EntityId> selectedNodePath,
        IReadOnlyDictionary<string, string> fieldValues,
        SubmissionSource source = SubmissionSource.Manual)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(selectedNodePath);
        ArgumentNullException.ThrowIfNull(fieldValues);

        var errors = new List<ValidationError>();

        var nodesById = new Dictionary<EntityId, TypificationNode>();
        foreach (var node in schema.Nodes)
        {
            nodesById.TryAdd(node.NodeId, node);
        }

        // --- Validate the selected node path is a valid root→leaf chain ---
        var pathValid = true;
        var pathNodes = new List<TypificationNode>(selectedNodePath.Count);

        if (selectedNodePath.Count == 0)
        {
            errors.Add(new ValidationError("path", "Selected node path must not be empty."));
            pathValid = false;
        }
        else
        {
            EntityId? previousId = null;
            for (var i = 0; i < selectedNodePath.Count; i++)
            {
                var id = selectedNodePath[i];
                if (!nodesById.TryGetValue(id, out var node))
                {
                    errors.Add(new ValidationError(
                        "path",
                        $"Selected node id '{id.Value}' does not exist in the schema."));
                    pathValid = false;
                    break;
                }

                pathNodes.Add(node);

                if (i == 0)
                {
                    if (node.ParentNodeId is not null)
                    {
                        errors.Add(new ValidationError(
                            "path",
                            $"First node '{node.Code}' must be a root (ParentNodeId == null)."));
                        pathValid = false;
                    }
                }
                else if (node.ParentNodeId != previousId)
                {
                    errors.Add(new ValidationError(
                        "path",
                        $"Node '{node.Code}' parent does not match the preceding node in the path."));
                    pathValid = false;
                }

                previousId = id;
            }

            if (pathValid)
            {
                var last = pathNodes[^1];
                if (!last.IsLeaf)
                {
                    errors.Add(new ValidationError(
                        "path",
                        $"The selected path must end at a leaf node (last node '{last.Code}' is not a leaf)."));
                    pathValid = false;
                }
            }
        }

        // Codes of the nodes in the (best-effort) path drive NodeSelected conditions.
        var selectedNodeCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in pathNodes)
        {
            selectedNodeCodes.Add(node.Code);
        }

        var selectedPathIds = new HashSet<EntityId>();
        foreach (var id in selectedNodePath)
        {
            selectedPathIds.Add(id);
        }

        // --- Field-level validation (active fields only) ---
        foreach (var field in schema.Fields)
        {
            var attachedActive = field.AttachToNodeId is not { } attachId || selectedPathIds.Contains(attachId);
            if (!attachedActive)
            {
                continue;
            }

            var visible = field.VisibleWhen is not { } cond
                || EvaluateCondition(cond, fieldValues, selectedNodeCodes);
            if (!visible)
            {
                continue;
            }

            var hasValue = fieldValues.TryGetValue(field.Key, out var rawValue)
                && !string.IsNullOrWhiteSpace(rawValue);

            if (field.Required && !hasValue)
            {
                errors.Add(new ValidationError(
                    field.Key,
                    $"Field '{field.Key}' is required."));
                continue;
            }

            if (!hasValue)
            {
                continue;
            }

            ValidateTypedValue(field, rawValue!, source, errors);
        }

        return errors.Count == 0 ? ValidationResult.Success : new ValidationResult(errors);
    }

    public bool EvaluateCondition(
        ConditionExpr expr,
        IReadOnlyDictionary<string, string> fieldValues,
        IReadOnlySet<string> selectedNodeCodes)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(fieldValues);
        ArgumentNullException.ThrowIfNull(selectedNodeCodes);

        var left = expr.RefType switch
        {
            ConditionRef.Field => fieldValues.GetValueOrDefault(expr.Ref) ?? string.Empty,
            ConditionRef.NodeSelected => selectedNodeCodes.Contains(expr.Ref) ? "true" : "false",
            _ => string.Empty,
        };

        var value = expr.Value ?? string.Empty;

        switch (expr.Op)
        {
            case ConditionOp.Eq:
                return left == value;
            case ConditionOp.Neq:
                return left != value;
            case ConditionOp.In:
                foreach (var part in value.Split(','))
                {
                    if (part.Trim() == left)
                    {
                        return true;
                    }
                }

                return false;
            case ConditionOp.Contains:
                return left.Contains(value, StringComparison.OrdinalIgnoreCase);
            case ConditionOp.Exists:
                return !string.IsNullOrEmpty(left);
            case ConditionOp.GreaterThan:
                return TryParseDouble(left, out var gl)
                    && TryParseDouble(value, out var gr)
                    && gl > gr;
            case ConditionOp.LessThan:
                return TryParseDouble(left, out var ll)
                    && TryParseDouble(value, out var lr)
                    && ll < lr;
            default:
                return false;
        }
    }

    private static void ValidateTypedValue(
        TypificationField field,
        string value,
        SubmissionSource source,
        List<ValidationError> errors)
    {
        switch (field.Type)
        {
            case FieldType.Number:
                if (!TryParseDouble(value, out var number))
                {
                    errors.Add(new ValidationError(
                        field.Key,
                        $"Field '{field.Key}' must be a number."));
                }
                else
                {
                    ApplyNumericRange(field, number, errors);
                }

                break;

            case FieldType.Date:
                if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    errors.Add(new ValidationError(
                        field.Key,
                        $"Field '{field.Key}' must be a valid date."));
                }

                break;

            case FieldType.Boolean:
                if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new ValidationError(
                        field.Key,
                        $"Field '{field.Key}' must be 'true' or 'false'."));
                }

                break;

            case FieldType.Select:
                if (!IsAllowedOption(field, value))
                {
                    errors.Add(new ValidationError(
                        field.Key,
                        $"Field '{field.Key}' has a value that is not an allowed option."));
                }

                break;

            case FieldType.MultiSelect:
                foreach (var part in value.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    if (!IsAllowedOption(field, trimmed))
                    {
                        errors.Add(new ValidationError(
                            field.Key,
                            $"Field '{field.Key}' contains a value that is not an allowed option: '{trimmed}'."));
                    }
                }

                break;

            case FieldType.Phone:
                if (!IsValidPhone(value))
                {
                    errors.Add(new ValidationError(
                        field.Key,
                        $"Field '{field.Key}' is not a valid phone number."));
                }

                break;

            case FieldType.Text:
            case FieldType.Textarea:
            case FieldType.Lookup:
                // Free-text types: the generic FieldValidation applies (below), and on the
                // AI write path an additional implicit length cap defends against huge blobs.
                break;

            default:
                break;
        }

        ApplyStringValidation(field, value, source, errors);
    }

    private static void ApplyStringValidation(
        TypificationField field,
        string value,
        SubmissionSource source,
        List<ValidationError> errors)
    {
        // Effective length cap = the smaller of the configured MaxLength and (for AI-sourced
        // free-text) the implicit AI cap. A single error is reported, so a field whose configured
        // MaxLength is already smaller than the AI cap is never double-reported.
        int? effectiveMaxLength = field.Validation?.MaxLength;
        if (source == SubmissionSource.AutoAi && field.Type.IsFreeText())
        {
            effectiveMaxLength = effectiveMaxLength is { } configured
                ? Math.Min(configured, AiFreeTextMaxLength)
                : AiFreeTextMaxLength;
        }

        if (effectiveMaxLength is { } maxLength && value.Length > maxLength)
        {
            // When the cap is the implicit AI cap (no configured MaxLength), word it as an
            // AI-sourced cap; otherwise it is the configured per-field maximum.
            var aiCapApplied = source == SubmissionSource.AutoAi
                && field.Type.IsFreeText()
                && field.Validation?.MaxLength is null;

            errors.Add(new ValidationError(
                field.Key,
                aiCapApplied
                    ? $"Field '{field.Key}' AI-sourced value exceeds the maximum length of {maxLength}."
                    : $"Field '{field.Key}' exceeds the maximum length of {maxLength}."));
        }

        if (field.Validation is not { } validation)
        {
            return;
        }

        if (validation.Regex is { Length: > 0 } pattern)
        {
            try
            {
                if (!Regex.IsMatch(value, pattern, RegexOptions.None, RegexTimeout))
                {
                    errors.Add(new ValidationError(
                        field.Key,
                        $"Field '{field.Key}' does not match the required format."));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add(new ValidationError(
                    field.Key,
                    $"Field '{field.Key}' could not be validated (pattern timed out)."));
            }
            catch (ArgumentException)
            {
                errors.Add(new ValidationError(
                    field.Key,
                    $"Field '{field.Key}' has an invalid validation pattern."));
            }
        }
    }

    private static void ApplyNumericRange(TypificationField field, double number, List<ValidationError> errors)
    {
        if (field.Validation is not { } validation)
        {
            return;
        }

        if (validation.Min is { } min && number < min)
        {
            errors.Add(new ValidationError(
                field.Key,
                $"Field '{field.Key}' must be at least {min.ToString(CultureInfo.InvariantCulture)}."));
        }

        if (validation.Max is { } max && number > max)
        {
            errors.Add(new ValidationError(
                field.Key,
                $"Field '{field.Key}' must be at most {max.ToString(CultureInfo.InvariantCulture)}."));
        }
    }

    private static bool IsAllowedOption(TypificationField field, string value)
    {
        if (field.Options is not { } options)
        {
            return false;
        }

        foreach (var option in options)
        {
            if (option.Value == value)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidPhone(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var digits = 0;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                digits++;
                continue;
            }

            if (ch is '+' or ' ' or '-' or '(' or ')')
            {
                continue;
            }

            return false;
        }

        return digits >= 5;
    }

    private static bool TryParseDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Depth = length of the parent chain (root depth 1). Sets <paramref name="cyclic"/>
    /// if a cycle is detected while walking parents.
    /// </summary>
    private static int ComputeDepth(
        TypificationNode node,
        Dictionary<EntityId, TypificationNode> nodesById,
        out bool cyclic)
    {
        cyclic = false;
        var depth = 1;
        var visited = new HashSet<EntityId> { node.NodeId };
        var current = node;

        while (current.ParentNodeId is { } parentId)
        {
            if (!nodesById.TryGetValue(parentId, out var parent))
            {
                // Missing parent reported separately; stop walking.
                break;
            }

            if (!visited.Add(parentId))
            {
                cyclic = true;
                return depth;
            }

            depth++;
            current = parent;
        }

        return depth;
    }
}
