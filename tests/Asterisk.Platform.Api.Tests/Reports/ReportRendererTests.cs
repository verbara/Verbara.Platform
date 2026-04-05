using System.Text;
using Asterisk.Platform.Api.Services.Reports;
using Asterisk.Platform.Core.Reports;

namespace Asterisk.Platform.Api.Tests.Reports;

public class ReportRendererTests
{
    private static ReportData CreateTestData() => new()
    {
        ReportName = "Test Report",
        TenantName = "Acme Corp",
        ReportType = "interval_summary",
        From = DateTimeOffset.UtcNow.AddDays(-1),
        To = DateTimeOffset.UtcNow,
        GeneratedAt = DateTimeOffset.UtcNow,
        Rows =
        [
            new("Queue A", new Dictionary<string, object>
            {
                ["Calls"] = 150, ["SLA %"] = 85.5, ["ASA (s)"] = 12.3,
            }),
            new("Queue B", new Dictionary<string, object>
            {
                ["Calls"] = 200, ["SLA %"] = 92.1, ["ASA (s)"] = 8.7,
            }),
        ],
        Summary = new Dictionary<string, double>
        {
            ["Total Calls"] = 350, ["Avg SLA %"] = 88.8,
        },
    };

    private static ReportData CreateEmptyData() => new()
    {
        ReportName = "Empty Report",
        TenantName = "Acme Corp",
        ReportType = "interval_summary",
        From = DateTimeOffset.UtcNow.AddDays(-1),
        To = DateTimeOffset.UtcNow,
        GeneratedAt = DateTimeOffset.UtcNow,
        Rows = null,
        Summary = null,
    };

    // ── PDF renderer ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_ShouldProduceNonEmptyBytes_WhenCalledOnPdfRenderer()
    {
        var renderer = new PdfReportRenderer();
        var data = CreateTestData();

        var bytes = await renderer.RenderAsync(data, CancellationToken.None);

        bytes.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RenderAsync_ShouldProducePdfMagicHeader_WhenCalledOnPdfRenderer()
    {
        var renderer = new PdfReportRenderer();
        var data = CreateTestData();

        var bytes = await renderer.RenderAsync(data, CancellationToken.None);

        var header = Encoding.ASCII.GetString(bytes, 0, 4);
        header.Should().Be("%PDF");
    }

    [Fact]
    public async Task RenderAsync_ShouldNotThrow_WhenPdfRendererReceivesEmptyData()
    {
        var renderer = new PdfReportRenderer();
        var data = CreateEmptyData();

        var act = async () => await renderer.RenderAsync(data, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RenderAsync_ShouldProduceNonEmptyBytes_WhenPdfRendererReceivesEmptyData()
    {
        var renderer = new PdfReportRenderer();
        var data = CreateEmptyData();

        var bytes = await renderer.RenderAsync(data, CancellationToken.None);

        bytes.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ContentType_ShouldReturnApplicationPdf_WhenQueriedOnPdfRenderer()
    {
        var renderer = new PdfReportRenderer();

        renderer.ContentType.Should().Be("application/pdf");
    }

    // ── CSV renderer ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_ShouldIncludeUtf8Bom_WhenCalledOnCsvRenderer()
    {
        var renderer = new CsvReportRenderer();
        var data = CreateTestData();

        var bytes = await renderer.RenderAsync(data, CancellationToken.None);

        var bom = Encoding.UTF8.GetPreamble();
        bytes.Should().StartWith(bom);
    }

    [Fact]
    public async Task RenderAsync_ShouldContainColumnHeaders_WhenCsvRendererHasDataRows()
    {
        var renderer = new CsvReportRenderer();
        var data = CreateTestData();

        var bytes = await renderer.RenderAsync(data, CancellationToken.None);
        var text = Encoding.UTF8.GetString(bytes);

        text.Should().Contain("Label");
        text.Should().Contain("Calls");
        text.Should().Contain("SLA %");
        text.Should().Contain("ASA (s)");
    }

    [Fact]
    public async Task RenderAsync_ShouldContainDataValues_WhenCsvRendererHasDataRows()
    {
        var renderer = new CsvReportRenderer();
        var data = CreateTestData();

        var bytes = await renderer.RenderAsync(data, CancellationToken.None);
        var text = Encoding.UTF8.GetString(bytes);

        text.Should().Contain("Queue A");
        text.Should().Contain("Queue B");
        text.Should().Contain("150");
        text.Should().Contain("200");
    }

    [Fact]
    public void ContentType_ShouldReturnTextCsv_WhenQueriedOnCsvRenderer()
    {
        var renderer = new CsvReportRenderer();

        renderer.ContentType.Should().Be("text/csv");
    }
}
