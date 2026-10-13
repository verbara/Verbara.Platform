using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests.Observability;

/// <summary>
/// measure-api-multi-replica-baseline (D1): the meters the Sdk and Pro OpenTelemetry lists leave out and
/// that the Api must enrol itself, so <c>/metrics</c> carries the Postgres pool (<c>Npgsql</c>) and the
/// auth write queue (<see cref="AuthWriteQueue.MeterName"/>).
/// </summary>
public sealed class CapacityMetersExtensionsTests
{
    [Fact]
    public void MeterNames_ShouldIncludeNpgsqlAndAuthWriteQueue_WhenEnumerated()
    {
        CapacityMetersExtensions.MeterNames.Should().Contain(
            "Npgsql", "the Postgres pool and operation instruments are published on the Npgsql meter");
        CapacityMetersExtensions.MeterNames.Should().Contain(
            AuthWriteQueue.MeterName, "the auth write-queue counters are published on that meter");
    }
}
