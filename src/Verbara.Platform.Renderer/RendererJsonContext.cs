using System.Text.Json.Serialization;
using Verbara.Platform.Core.Reports;

namespace Verbara.Platform.Renderer;

internal sealed record HealthResponse(string Status, int ActiveConcurrency, int MaxConcurrency);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReportData))]
[JsonSerializable(typeof(ReportDataRow))]
[JsonSerializable(typeof(IReadOnlyList<ReportDataRow>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, object>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class RendererJsonContext : JsonSerializerContext;
