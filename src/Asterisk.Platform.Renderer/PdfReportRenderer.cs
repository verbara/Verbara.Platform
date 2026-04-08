using Asterisk.Platform.Core.Reports;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ScottPlot;
using PdfColors = QuestPDF.Helpers.Colors;
using SpImageFormat = ScottPlot.ImageFormat;

namespace Asterisk.Platform.Renderer;

internal sealed class PdfReportRenderer : IReportRenderer
{
    public string ContentType => "application/pdf";
    public string FileExtension => "pdf";

    public ValueTask<byte[]> RenderAsync(ReportData data, CancellationToken ct)
    {
        var pdfBytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(30);

                page.Header().Element(c => BuildHeader(c, data));
                page.Content().Element(c => BuildContent(c, data));
                page.Footer().Element(BuildFooter);
            });
        }).GeneratePdf();

        return ValueTask.FromResult(pdfBytes);
    }

    private static void BuildHeader(IContainer container, ReportData data)
    {
        container.Column(col =>
        {
            col.Item().Text(data.ReportName)
                .FontSize(18).Bold().FontColor(PdfColors.Grey.Darken4);

            col.Item().DefaultTextStyle(s => s.FontSize(10).FontColor(PdfColors.Grey.Darken2)).Text(t =>
            {
                t.Span(data.TenantName).Bold();
                t.Span("   |   ").FontColor(PdfColors.Grey.Medium);
                t.Span("Period: ").Bold();
                t.Span($"{data.From:yyyy-MM-dd} — {data.To:yyyy-MM-dd}");
            });

            col.Item().PaddingTop(4).LineHorizontal(1).LineColor(PdfColors.Grey.Lighten2);
        });
    }

    private static string ResolveHeaderColor(ReportData data) =>
        string.IsNullOrWhiteSpace(data.PrimaryColor) ? PdfColors.Blue.Darken3 : data.PrimaryColor;

    private static void BuildContent(IContainer container, ReportData data)
    {
        var headerColor = ResolveHeaderColor(data);

        container.Column(col =>
        {
            var rows = data.Rows;

            // Bar chart — first numeric column
            var chartBytes = TryBuildChart(data);
            if (chartBytes is not null)
            {
                col.Item().PaddingVertical(12)
                    .AlignCenter()
                    .Width(600)
                    .Height(200)
                    .Image(chartBytes);
            }

            if (rows is { Count: > 0 })
            {
                var headers = rows[0].Columns.Keys.ToArray();

                col.Item().Table(table =>
                {
                    // Define columns
                    table.ColumnsDefinition(def =>
                    {
                        def.RelativeColumn(2); // Label
                        foreach (var _ in headers)
                            def.RelativeColumn();
                    });

                    // Header row
                    IContainer HeaderCell(IContainer c) =>
                        c.Background(headerColor).Padding(4);

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell)
                            .Text("Label").FontColor(PdfColors.White).Bold().FontSize(9);
                        foreach (var h in headers)
                        {
                            header.Cell().Element(HeaderCell)
                                .Text(h).FontColor(PdfColors.White).Bold().FontSize(9);
                        }
                    });

                    // Data rows with alternating colors
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        var bg = i % 2 == 0 ? PdfColors.White : PdfColors.Grey.Lighten4;

                        IContainer DataCell(IContainer c) =>
                            c.Background(bg).Padding(4);

                        table.Cell().Element(DataCell)
                            .Text(row.Label).FontSize(8);

                        foreach (var h in headers)
                        {
                            var val = row.Columns.TryGetValue(h, out var v) ? v?.ToString() ?? string.Empty : string.Empty;
                            table.Cell().Element(DataCell)
                                .Text(val).FontSize(8);
                        }
                    }
                });
            }
            else
            {
                col.Item().PaddingVertical(20)
                    .AlignCenter()
                    .Text("No data available for this report period.")
                    .FontSize(11).FontColor(PdfColors.Grey.Medium).Italic();
            }

            // Summary section
            if (data.Summary is { Count: > 0 })
            {
                col.Item().PaddingTop(12).Text("Summary").Bold().FontSize(12);
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(def =>
                    {
                        def.RelativeColumn(3);
                        def.RelativeColumn(2);
                    });

                    table.Header(header =>
                    {
                        header.Cell().Background(headerColor).Padding(4)
                            .Text("Metric").FontColor(PdfColors.White).Bold().FontSize(9);
                        header.Cell().Background(headerColor).Padding(4)
                            .Text("Value").FontColor(PdfColors.White).Bold().FontSize(9);
                    });

                    var idx = 0;
                    foreach (var kv in data.Summary)
                    {
                        var bg = idx++ % 2 == 0 ? PdfColors.White : PdfColors.Grey.Lighten4;
                        table.Cell().Background(bg).Padding(4)
                            .Text(kv.Key).FontSize(8);
                        table.Cell().Background(bg).Padding(4)
                            .Text(kv.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture)).FontSize(8);
                    }
                });
            }
        });
    }

    private static void BuildFooter(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().DefaultTextStyle(s => s.FontSize(8)).Text(t =>
            {
                t.Span("Generated: ").FontColor(PdfColors.Grey.Medium);
                t.Span(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", System.Globalization.CultureInfo.InvariantCulture))
                    .FontColor(PdfColors.Grey.Medium);
            });

            row.RelativeItem().AlignRight().DefaultTextStyle(s => s.FontSize(8)).Text(t =>
            {
                t.Span("Page ").FontColor(PdfColors.Grey.Medium);
                t.CurrentPageNumber().FontColor(PdfColors.Grey.Medium);
                t.Span(" of ").FontColor(PdfColors.Grey.Medium);
                t.TotalPages().FontColor(PdfColors.Grey.Medium);
            });
        });
    }

    private static byte[]? TryBuildChart(ReportData data)
    {
        if (data.Rows is not { Count: > 0 }) return null;

        // Find first numeric column
        var headers = data.Rows[0].Columns.Keys.ToArray();
        string? numericColumn = null;
        foreach (var h in headers)
        {
            if (data.Rows[0].Columns.TryGetValue(h, out var sample) &&
                sample is double or float or int or long or decimal)
            {
                numericColumn = h;
                break;
            }
        }

        // Try string-parseable numeric if no boxed numeric found
        if (numericColumn is null)
        {
            foreach (var h in headers)
            {
                if (data.Rows[0].Columns.TryGetValue(h, out var sample) &&
                    sample?.ToString() is { } s &&
                    double.TryParse(s, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    numericColumn = h;
                    break;
                }
            }
        }

        if (numericColumn is null) return null;

        var values = new double[data.Rows.Count];
        for (var i = 0; i < data.Rows.Count; i++)
        {
            if (data.Rows[i].Columns.TryGetValue(numericColumn, out var v))
            {
                values[i] = v switch
                {
                    double d => d,
                    float f => f,
                    int n => n,
                    long l => l,
                    decimal dec => (double)dec,
                    _ when double.TryParse(v?.ToString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => 0.0
                };
            }
        }

        var plot = new Plot();
        var barPlot = plot.Add.Bars(values);
        barPlot.ValueLabel = numericColumn;
        plot.Title(numericColumn);
        plot.Axes.AutoScale();

        return plot.GetImageBytes(600, 200, SpImageFormat.Png);
    }
}
