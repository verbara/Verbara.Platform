using System.Text;
using Verbara.Platform.Api.Services.Reports;
using Verbara.Platform.Core.Reports;

namespace Verbara.Platform.Api.Tests.Reports;

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

    // ── CSV renderer (still in Platform.Api) ─────────────────────────────────

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
