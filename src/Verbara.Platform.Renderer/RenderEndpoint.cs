using Verbara.Platform.Core.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Renderer;

internal static partial class RenderEndpoint
{
    private static readonly SemaphoreSlim ConcurrencySemaphore = new(3, 3);

    internal static void MapRenderEndpoint(this WebApplication app)
    {
        app.MapPost("/api/v1/render", HandleRenderAsync);
    }

    private static async Task<IResult> HandleRenderAsync(
        [FromQuery] string? format,
        [FromBody] ReportData data,
        [FromKeyedServices("pdf")] IReportRenderer pdfRenderer,
        [FromKeyedServices("csv")] IReportRenderer csvRenderer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Verbara.Platform.Renderer.RenderEndpoint");
        var resolvedFormat = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "pdf";
        var renderer = resolvedFormat == "csv" ? csvRenderer : pdfRenderer;

        if (!ConcurrencySemaphore.Wait(0, CancellationToken.None))
        {
            LogConcurrencyExceeded(logger);
            return Results.StatusCode(503);
        }

        try
        {
            LogRenderStarted(logger, resolvedFormat, data.ReportName);
            var bytes = await renderer.RenderAsync(data, ct);
            var fileName = $"{data.ReportType}_{data.From:yyyyMMdd}_{data.To:yyyyMMdd}.{renderer.FileExtension}";

            LogRenderCompleted(logger, resolvedFormat, data.ReportName, bytes.Length);
            return Results.File(bytes, renderer.ContentType, fileName);
        }
        finally
        {
            ConcurrencySemaphore.Release();
        }
    }

    internal static int ActiveConcurrency => 3 - ConcurrencySemaphore.CurrentCount;
    internal static int MaxConcurrency => 3;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Render request rejected: max concurrency ({MaxConcurrency}) exceeded")]
    private static partial void LogConcurrencyExceeded(ILogger logger, int maxConcurrency = 3);

    [LoggerMessage(Level = LogLevel.Information, Message = "Render started: format={Format}, report={ReportName}")]
    private static partial void LogRenderStarted(ILogger logger, string format, string reportName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Render completed: format={Format}, report={ReportName}, size={SizeBytes} bytes")]
    private static partial void LogRenderCompleted(ILogger logger, string format, string reportName, int sizeBytes);
}
