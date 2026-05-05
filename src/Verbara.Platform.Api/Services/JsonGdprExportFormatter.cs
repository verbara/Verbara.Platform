using System.Text.Json;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

internal sealed class JsonGdprExportFormatter : IGdprExportFormatter
{
    public string ContentType => "application/json";
    public string FileExtension => ".json";

    public ValueTask<byte[]> FormatAsync(GdprExportData data, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            data,
            ApiJsonContext.Default.GdprExportData);
        return ValueTask.FromResult(bytes);
    }
}
