using System.Net.Http.Json;
using Asterisk.Platform.Core.Reports;

namespace Asterisk.Platform.Api.Services.Reports;

internal sealed partial class HttpPdfReportRenderer(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpPdfReportRenderer> logger) : IReportRenderer
{
    public string ContentType => "application/pdf";
    public string FileExtension => "pdf";

    public async ValueTask<byte[]> RenderAsync(ReportData data, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("renderer");
        using var response = await client.PostAsJsonAsync(
            "/api/v1/render?format=pdf", data, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            LogRenderFailed((int)response.StatusCode, data.ReportName);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "PDF render failed with status {StatusCode} for report {ReportName}")]
    private partial void LogRenderFailed(int statusCode, string reportName);
}
