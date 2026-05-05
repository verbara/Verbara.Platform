using Verbara.Platform.Core.Reports;

namespace Verbara.Platform.Api.Services.Reports;

/// <summary>
/// Holds all registered <see cref="IReportDataBuilder"/> implementations keyed by report type.
/// </summary>
internal sealed class ReportDataBuilderRegistry
{
    private readonly Dictionary<string, IReportDataBuilder> _builders;

    public ReportDataBuilderRegistry(IEnumerable<IReportDataBuilder> builders)
    {
        _builders = builders.ToDictionary(
            b => b.ReportType,
            b => b,
            StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetBuilder(string reportType, out IReportDataBuilder builder)
        => _builders.TryGetValue(reportType, out builder!);

    public IReadOnlyCollection<string> RegisteredTypes => _builders.Keys;
}
