using Asterisk.Platform.Api.Services;
using Npgsql;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// ADR-0015 Phase 1 — coverage for the SMB-tier pool sizing defaults helper.
/// Verifies (a) sane defaults are applied when the operator left them blank,
/// (b) operator-supplied values are preserved verbatim, (c) partial
/// overrides only fill the missing keys, (d) null/empty inputs are
/// idempotent.
/// </summary>
public sealed class ConnectionStringDefaultsTests
{
    private const string MinimalConnectionString =
        "Host=postgres;Database=platform;Username=u;Password=p";

    [Fact]
    public void ApplyPoolDefaults_ShouldApplyAllDefaults_WhenOperatorDidNotSpecify()
    {
        var result = ConnectionStringDefaults.ApplyPoolDefaults(MinimalConnectionString);

        var parsed = new NpgsqlConnectionStringBuilder(result);
        parsed.MaxPoolSize.Should().Be(ConnectionStringDefaults.DefaultMaximumPoolSize);
        parsed.MinPoolSize.Should().Be(ConnectionStringDefaults.DefaultMinimumPoolSize);
        parsed.ConnectionIdleLifetime.Should().Be(ConnectionStringDefaults.DefaultConnectionIdleLifetimeSeconds);
    }

    [Fact]
    public void ApplyPoolDefaults_ShouldPreserveOperatorOverride_WhenMaxPoolSizeSpecified()
    {
        var input = $"{MinimalConnectionString};Maximum Pool Size=50";

        var result = ConnectionStringDefaults.ApplyPoolDefaults(input);

        var parsed = new NpgsqlConnectionStringBuilder(result);
        parsed.MaxPoolSize.Should().Be(50);
    }

    [Fact]
    public void ApplyPoolDefaults_ShouldPreservePartialOverride_FillingOnlyMissingKeys()
    {
        var input = $"{MinimalConnectionString};Maximum Pool Size=20";

        var result = ConnectionStringDefaults.ApplyPoolDefaults(input);

        var parsed = new NpgsqlConnectionStringBuilder(result);
        parsed.MaxPoolSize.Should().Be(20); // operator-specified
        parsed.MinPoolSize.Should().Be(ConnectionStringDefaults.DefaultMinimumPoolSize); // default
        parsed.ConnectionIdleLifetime.Should().Be(ConnectionStringDefaults.DefaultConnectionIdleLifetimeSeconds); // default
    }

    [Fact]
    public void ApplyPoolDefaults_ShouldReturnNull_WhenInputIsNull()
    {
        ConnectionStringDefaults.ApplyPoolDefaults(null).Should().BeNull();
    }

    [Fact]
    public void ApplyPoolDefaults_ShouldReturnUnchanged_WhenInputIsWhitespace()
    {
        ConnectionStringDefaults.ApplyPoolDefaults("   ").Should().Be("   ");
    }
}
