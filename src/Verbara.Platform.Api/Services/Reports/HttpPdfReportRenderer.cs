using System.Net.Http.Json;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core.Reports;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services.Reports;

internal sealed partial class HttpPdfReportRenderer : IReportRenderer
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps the upstream
    /// PDF renderer POST. Circuit 3/120s + retry 1/1s + timeout 30s (registered in Program.cs).
    /// </summary>
    public const string ResiliencePolicyKey = "report.pdf-render";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpPdfReportRenderer> _logger;
    private readonly ResiliencePolicy _policy;

    public HttpPdfReportRenderer(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpPdfReportRenderer> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public string ContentType => "application/pdf";
    public string FileExtension => "pdf";

    public async ValueTask<byte[]> RenderAsync(ReportData data, CancellationToken ct)
    {
        // Transient-retry wrap: circuit breaker + single retry for blips in the upstream
        // renderer. The 30s policy-level timeout is enforced via ResiliencePolicy itself.
        return await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                using var client = _httpClientFactory.CreateClient("renderer");
                using var response = await client.PostAsJsonAsync(
                    "/api/v1/render?format=pdf", data, ApiJsonContext.Default.ReportData, innerCt).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LogRenderFailed((int)response.StatusCode, data.ReportName);
                    response.EnsureSuccessStatusCode();
                }

                return await response.Content.ReadAsByteArrayAsync(innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "PDF render failed with status {StatusCode} for report {ReportName}")]
    private partial void LogRenderFailed(int statusCode, string reportName);
}
