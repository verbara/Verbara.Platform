using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Default <see cref="ITypificationAiClassifier"/>: a single-shot, direct LLM classifier
/// (P2a). It renders the schema's channel-applicable leaves and capture fields into a
/// system prompt, sends the transcript as the user turn, then defensively parses the
/// model's JSON object and maps the returned <c>leafCode</c> to a validated root→leaf
/// node path (mirroring the prefill resolver's Code→node + parent-chain walk +
/// sub-tree validation, never-throws style).
/// <para>
/// Every failure mode — empty transcript, malformed output, an unknown/non-leaf code, a
/// leaf outside the bound sub-tree, or any transport/timeout exception — degrades to
/// <see langword="null"/> so the caller can fall back to manual disposition.
/// </para>
/// </summary>
public sealed class DefaultTypificationAiClassifier : ITypificationAiClassifier
{
    // Defensive transcript caps: classification only needs recent context, and the model
    // has a finite window. Keep at most the last N messages and the last M characters of
    // the assembled text (whichever bites first), measured from the END so the most
    // recent turns survive.
    private const int MaxTranscriptMessages = 40;
    private const int MaxTranscriptChars = 6000;

    // Low temperature: classification is a deterministic mapping task, not creative.
    private const double ClassifyTemperature = 0.1;

    // Enough for a small JSON object with a handful of captured fields.
    private const int ClassifyMaxTokens = 400;

    /// <summary>
    /// Classifier prompt template version. Bump this constant whenever the system prompt
    /// changes so that persisted <see cref="AiSuggestionRecord"/>s carry correct provenance.
    /// </summary>
    internal const string CurrentPromptVersion = "p2b-1";

    private readonly ILlmProvider _llm;
    private readonly string _modelId;

    public DefaultTypificationAiClassifier(ILlmProvider llm, IOptions<LlmProviderOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(llm);
        _llm = llm;
        _modelId = options?.Value.Model is { Length: > 0 } m ? m : "unknown";
    }

    public async Task<AiClassification?> ClassifyAsync(
        TypificationSchema schema,
        EntityId? subtreeRoot,
        Conversation conversation,
        IReadOnlyList<Message> transcript,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(transcript);

        var transcriptText = BuildTranscriptText(transcript);
        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            // Nothing to classify (no text blocks across the supplied messages).
            return null;
        }

        // Candidate leaves: leaf nodes applicable to this conversation's channel, and
        // (when bound) under the sub-tree root.
        var leaves = CollectCandidateLeaves(schema, subtreeRoot, conversation.Channel);
        if (leaves.Count == 0)
        {
            // No leaf the model could legally pick → no classification possible.
            return null;
        }

        var systemPrompt = BuildSystemPrompt(schema, leaves);

        var request = new LlmRequest(
            Messages:
            [
                new LlmMessage("system", systemPrompt),
                new LlmMessage("user", transcriptText),
            ],
            Temperature: ClassifyTemperature,
            MaxTokens: ClassifyMaxTokens);

        LlmResponse response;
        try
        {
            response = await _llm.CompleteAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Honor genuine cancellation by propagating; everything else degrades to null.
            throw;
        }
        catch
        {
            // Transport/timeout/HTTP/provider failure → degrade to manual disposition.
            return null;
        }

        var parsed = TryParse(response.Content);
        if (parsed is null)
            return null;

        return MapAndValidate(schema, subtreeRoot, parsed, _modelId, CurrentPromptVersion);
    }

    /// <summary>
    /// Assembles a role-prefixed plain-text transcript from the ordered messages,
    /// concatenating each message's <see cref="TextBlock"/> contents and skipping
    /// non-text blocks. Capped (most-recent-wins) for window safety.
    /// </summary>
    private static string BuildTranscriptText(IReadOnlyList<Message> transcript)
    {
        // Keep only the last MaxTranscriptMessages messages.
        var start = transcript.Count > MaxTranscriptMessages
            ? transcript.Count - MaxTranscriptMessages
            : 0;

        var lines = new List<string>(transcript.Count - start);
        for (var i = start; i < transcript.Count; i++)
        {
            var message = transcript[i];
            var text = ExtractText(message.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var role = message.Direction == MessageDirection.Inbound ? "Customer: " : "Agent: ";
            lines.Add(role + text);
        }

        if (lines.Count == 0)
            return string.Empty;

        var joined = string.Join('\n', lines);

        // Char cap (keep the tail — most recent context).
        if (joined.Length > MaxTranscriptChars)
            joined = joined[^MaxTranscriptChars..];

        return joined;
    }

    private static string ExtractText(MessageEnvelope envelope)
    {
        StringBuilder? sb = null;
        foreach (var block in envelope.Blocks)
        {
            if (block is not TextBlock { Text: { Length: > 0 } text })
                continue;

            sb ??= new StringBuilder();
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(text);
        }

        return sb?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Leaf nodes applicable to <paramref name="channel"/> and, when
    /// <paramref name="subtreeRoot"/> is set, under that sub-tree.
    /// </summary>
    private static List<TypificationNode> CollectCandidateLeaves(
        TypificationSchema schema, EntityId? subtreeRoot, ChannelType channel)
    {
        var byId = BuildNodeIndex(schema);

        var leaves = new List<TypificationNode>();
        foreach (var node in schema.Nodes)
        {
            if (!node.IsLeaf)
                continue;

            if (node.ChannelApplicability is { } channels && !channels.Contains(channel))
                continue;

            if (subtreeRoot is { } root && !PathContains(node, root, byId))
                continue;

            leaves.Add(node);
        }

        return leaves;
    }

    /// <summary>Renders the system prompt: instructions + leaf catalog + field catalog.</summary>
    private static string BuildSystemPrompt(TypificationSchema schema, List<TypificationNode> leaves)
    {
        var byId = BuildNodeIndex(schema);
        var sb = new StringBuilder(1024);

        sb.Append(
            "You are a contact-center disposition classifier. Read the conversation transcript " +
            "and choose the single best-matching outcome (leaf) from the catalog below.\n\n");

        sb.Append("OUTCOMES (pick exactly one code):\n");
        foreach (var leaf in leaves)
        {
            // "<Code>: <Label>  (path: <ancestor Labels joined by ' > '>)"
            sb.Append("- ")
              .Append(leaf.Code)
              .Append(": ")
              .Append(leaf.Label)
              .Append("  (path: ")
              .Append(BuildAncestorPathLabels(leaf, byId))
              .Append(")\n");
        }

        sb.Append("\nFIELDS (optional capture, use only these keys):\n");
        if (schema.Fields.Count == 0)
        {
            sb.Append("- (none)\n");
        }
        else
        {
            foreach (var field in schema.Fields)
            {
                // "<Key> (<Type>): <Label>"
                sb.Append("- ")
                  .Append(field.Key)
                  .Append(" (")
                  .Append(field.Type)
                  .Append("): ")
                  .Append(field.Label)
                  .Append('\n');
            }
        }

        sb.Append(
            "\nRespond with ONLY a JSON object: " +
            "{\"leafCode\": \"<one of the codes>\", \"confidence\": <0.0-1.0>, " +
            "\"sentiment\": \"positive|neutral|negative|very_negative\", " +
            "\"fields\": {\"<key>\": \"<value>\"}}. " +
            "Use only the listed codes and field keys. " +
            "If unsure, pick the closest leaf and a low confidence.");

        return sb.ToString();
    }

    /// <summary>Ancestor labels root→leaf, joined by " > " (includes the leaf itself).</summary>
    private static string BuildAncestorPathLabels(
        TypificationNode leaf, Dictionary<EntityId, TypificationNode> byId)
    {
        var labels = new List<string>();
        var current = (TypificationNode?)leaf;
        var guard = byId.Count + 1; // cycle guard (malformed parent chains never loop forever).

        while (current is { } node && guard-- > 0)
        {
            labels.Add(node.Label);
            current = node.ParentNodeId is { } parentId && byId.TryGetValue(parentId, out var parent)
                ? parent
                : null;
        }

        labels.Reverse();
        return string.Join(" > ", labels);
    }

    /// <summary>
    /// Defensive parse: strips Markdown fences / leading prose by slicing from the first
    /// '{' to the last '}', then source-gen deserializes. Returns null on any failure —
    /// NEVER throws.
    /// </summary>
    private static AiClassificationResult? TryParse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var open = content.IndexOf('{');
        var close = content.LastIndexOf('}');
        if (open < 0 || close <= open)
            return null;

        var slice = content[open..(close + 1)];

        try
        {
            return JsonSerializer.Deserialize(slice, TypificationAiJsonContext.Default.AiClassificationResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps the parsed <c>leafCode</c> to a validated root→leaf path (mirroring the
    /// prefill resolver): the code must resolve to an existing node that is a leaf; walk
    /// its parent chain to the root; when a sub-tree root is bound the path must contain
    /// it. Unknown field keys are dropped; confidence is clamped to [0, 1].
    /// </summary>
    private static AiClassification? MapAndValidate(
        TypificationSchema schema, EntityId? subtreeRoot, AiClassificationResult result,
        string modelId, string promptVersion)
    {
        if (string.IsNullOrWhiteSpace(result.leafCode))
            return null;

        // Code → node lookup (Code is unique within the schema; Ordinal match).
        TypificationNode? leaf = null;
        foreach (var node in schema.Nodes)
        {
            if (string.Equals(node.Code, result.leafCode, StringComparison.Ordinal))
            {
                leaf = node;
                break;
            }
        }

        if (leaf is null || !leaf.IsLeaf)
            return null;

        var byId = BuildNodeIndex(schema);

        // Walk parent chain leaf→root, then reverse to root→leaf.
        var reversed = new List<EntityId>();
        var current = (TypificationNode?)leaf;
        var guard = byId.Count + 1; // cycle guard.
        while (current is { } node && guard-- > 0)
        {
            reversed.Add(node.NodeId);
            current = node.ParentNodeId is { } parentId && byId.TryGetValue(parentId, out var parent)
                ? parent
                : null;
        }

        reversed.Reverse();
        var path = reversed;

        // Sub-tree respect: a bound sub-tree root must be IN the resolved path.
        if (subtreeRoot is { } root && !path.Contains(root))
            return null;

        var fieldValues = FilterKnownFields(schema, result.fields);
        var confidence = Math.Clamp(result.confidence, 0.0, 1.0);
        var sentiment = string.IsNullOrWhiteSpace(result.sentiment) ? null : result.sentiment;

        return new AiClassification(path, fieldValues, confidence, sentiment, modelId, promptVersion);
    }

    /// <summary>Keeps only field values whose key exists in the schema (drops invented keys).</summary>
    private static Dictionary<string, string> FilterKnownFields(
        TypificationSchema schema, IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null || fields.Count == 0)
            return new Dictionary<string, string>(0, StringComparer.Ordinal);

        var knownKeys = new HashSet<string>(schema.Fields.Count, StringComparer.Ordinal);
        foreach (var field in schema.Fields)
            knownKeys.Add(field.Key);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (key is not null && value is not null && knownKeys.Contains(key))
                values[key] = value;
        }

        return values;
    }

    private static Dictionary<EntityId, TypificationNode> BuildNodeIndex(TypificationSchema schema)
    {
        var byId = new Dictionary<EntityId, TypificationNode>(schema.Nodes.Count);
        foreach (var node in schema.Nodes)
            byId[node.NodeId] = node;
        return byId;
    }

    /// <summary>True when <paramref name="root"/> is on the parent chain of <paramref name="node"/> (inclusive).</summary>
    private static bool PathContains(
        TypificationNode node, EntityId root, Dictionary<EntityId, TypificationNode> byId)
    {
        var current = (TypificationNode?)node;
        var guard = byId.Count + 1; // cycle guard.
        while (current is { } n && guard-- > 0)
        {
            if (n.NodeId == root)
                return true;

            current = n.ParentNodeId is { } parentId && byId.TryGetValue(parentId, out var parent)
                ? parent
                : null;
        }

        return false;
    }
}
