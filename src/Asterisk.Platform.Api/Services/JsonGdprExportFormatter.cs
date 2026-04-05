using System.Text.Json;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Services;

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
