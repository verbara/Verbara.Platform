using System.Text.Json;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Resolution;

/// <summary>
/// Default <see cref="ITypificationPrefillResolver"/>: a pure (no I/O) wrap-up
/// prefill resolver.
/// <para>
/// <b>Reason path (node preselect):</b> reads <c>Conversation.Metadata["reasonPath"]</c>
/// — a JSON array of node Codes captured root→leaf upstream — and walks it against
/// <see cref="TypificationSchema.Nodes"/>, validating the parent-child chain. It keeps
/// the longest valid prefix, stopping at the first Code missing from the schema or
/// breaking the chain. When a <c>subtreeRoot</c> is supplied the resolved path must
/// pass through that node, otherwise an empty path is returned (the captured reason is
/// outside the binding's branch). Malformed/absent metadata degrades to an empty path;
/// this method never throws.
/// </para>
/// <para>
/// <b>Field prefill (values):</b> for each schema field whose
/// <see cref="TypificationField.PrefillSource"/> is a
/// <see cref="PrefillSourceKind.Metadata"/> ref present in the conversation metadata,
/// the value is projected keyed by the field's <see cref="TypificationField.Key"/>
/// (matching the Web form's fieldValues keying). DataDip kinds (P3) and absent keys are
/// ignored.
/// </para>
/// <para>
/// <b>AI-entity prefill is NOT resolved here.</b> It is delivered via the AI suggestion
/// path — the classifier's screened entity field values surfaced through
/// <c>/typification-suggestion</c> — not this pure metadata resolver, which has no
/// suggestion at GET-form time (the Web form fires the suggestion request only AFTER the
/// form loads, then seeds those field values on Accept/AutoFill). Adding
/// <see cref="PrefillSourceKind.AiEntity"/> resolution here would be a no-op at load time.
/// </para>
/// </summary>
public sealed class DefaultTypificationPrefillResolver : ITypificationPrefillResolver
{
    public PrefillResult ResolvePrefill(TypificationSchema schema, EntityId? subtreeRoot, Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(conversation);

        var nodePath = ResolveNodePath(schema, subtreeRoot, conversation);
        var fieldValues = ResolveFieldValues(schema, conversation);

        return new PrefillResult(nodePath, fieldValues);
    }

    private static List<EntityId> ResolveNodePath(
        TypificationSchema schema, EntityId? subtreeRoot, Conversation conversation)
    {
        if (!conversation.Metadata.TryGetValue(TypificationMetadataKeys.ReasonPath, out var json)
            || string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        string[]? codes;
        try
        {
            codes = JsonSerializer.Deserialize(json, TypificationJsonContext.Default.StringArray);
        }
        catch (JsonException)
        {
            // Malformed reasonPath → degrade to no preselect, never throw.
            return [];
        }

        if (codes is null || codes.Length == 0)
            return [];

        // Code → node lookup (Code is unique within the schema).
        var byCode = new Dictionary<string, TypificationNode>(schema.Nodes.Count, StringComparer.Ordinal);
        foreach (var node in schema.Nodes)
            byCode[node.Code] = node;

        // Walk the captured Codes building the longest valid parent-child chain.
        // index 0: any node is a valid start (it may be a true root, or the captured
        //          path may begin mid-tree at the binding's sub-tree root).
        // index i>0: require codes[i].ParentNodeId == codes[i-1].NodeId.
        var path = new List<EntityId>(codes.Length);
        EntityId? previousNodeId = null;
        foreach (var code in codes)
        {
            if (code is null || !byCode.TryGetValue(code, out var node))
                break;

            if (previousNodeId is { } parent && node.ParentNodeId != parent)
                break;

            path.Add(node.NodeId);
            previousNodeId = node.NodeId;
        }

        if (path.Count == 0)
            return [];

        // Sub-tree respect: when a sub-tree root is bound, the resolved path must pass
        // THROUGH it; otherwise the captured reason is outside this branch → no preselect.
        if (subtreeRoot is { } root && !path.Contains(root))
            return [];

        return path;
    }

    private static Dictionary<string, string> ResolveFieldValues(
        TypificationSchema schema, Conversation conversation)
    {
        Dictionary<string, string>? values = null;
        foreach (var field in schema.Fields)
        {
            if (field.PrefillSource is not { Kind: PrefillSourceKind.Metadata } prefill)
                continue;

            if (!conversation.Metadata.TryGetValue(prefill.Ref, out var value))
                continue;

            values ??= new Dictionary<string, string>(StringComparer.Ordinal);
            values[field.Key] = value;
        }

        return values ?? new Dictionary<string, string>(0, StringComparer.Ordinal);
    }
}
