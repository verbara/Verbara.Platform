using System.Text.Json.Serialization;

namespace Verbara.Platform.Typification;

/// <summary>
/// Source-generated (reflection-free, Native AOT-safe) JSON context for the
/// typification capture pipeline. Currently covers the <c>reasonPath</c> contract:
/// a JSON array of node Codes (root→leaf), deserialized via
/// <c>TypificationJsonContext.Default.StringArray</c>.
/// </summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class TypificationJsonContext : JsonSerializerContext;
