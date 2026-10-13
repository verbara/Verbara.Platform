using Npgsql;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests.Observability;

/// <summary>
/// measure-api-multi-replica-baseline (D2): <see cref="DatabaseMigrationService.ApplyMigrations"/> builds
/// its own data source. Npgsql reports a data source's pool on <c>/metrics</c> from construction until
/// it is disposed, so one the runner leaks keeps an unnamed pool, labelled with its connection string,
/// on every scrape for the life of the process. No database: the connection is refused.
/// </summary>
public sealed class DatabaseMigrationServiceDataSourceTests
{
    [Fact]
    public void ApplyMigrations_ShouldReleaseItsDataSource_WhenConnectionFails()
    {
        var marker = Guid.NewGuid().ToString("N");

        var act = () => DatabaseMigrationService.ApplyMigrations(
            $"Host=127.0.0.1;Port=1;Timeout=1;Application Name={marker}");

        act.Should().Throw<NpgsqlException>();
        NpgsqlPoolTags.ReportedNow().Should().NotContain(tag => tag.Contains(marker, StringComparison.Ordinal),
            "the runner's data source must be disposed even when its connection fails");
    }
}
