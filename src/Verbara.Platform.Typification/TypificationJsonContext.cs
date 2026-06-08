using System.Text.Json.Serialization;

namespace Verbara.Platform.Typification;

/// <summary>
/// Source-generated (reflection-free, Native AOT-safe) JSON context for the
/// typification capture pipeline. Currently covers the <c>reasonPath</c> contract:
/// a JSON array of node Codes (root→leaf), (de)serialized via
/// <c>TypificationJsonContext.Default.StringArray</c>.
/// <para>
/// <b>Public</b> so capture writers outside this assembly (e.g. the Flows
/// <c>collect_reason</c> node) can serialize the same <c>string[]</c> Codes contract
/// through the one source-gen context instead of duplicating it — keeping a single
/// reflection-free serializer for <c>reasonPath</c>.
/// </para>
/// </summary>
[JsonSerializable(typeof(string[]))]
public sealed partial class TypificationJsonContext : JsonSerializerContext;
