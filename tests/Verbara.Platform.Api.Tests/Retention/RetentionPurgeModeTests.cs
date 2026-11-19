using System.Collections.Concurrent;
using Verbara.Platform.Api.Endpoints.Retention;
using Verbara.Sdk.Pro.Storage.Common.Retention;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Tests.Retention;

/// <summary>
/// openspec change <c>retention-dry-run-is-configurable</c>, tasks 1.3 and 1.4 — the host hands the configured mode
/// to the Pro purge (<see cref="RetentionOptions.DryRun"/>), the read-only accessor reports that same value, and the
/// startup log names the key.
/// </summary>
public sealed class RetentionPurgeModeTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;

    public RetentionPurgeModeTests(PlatformAdminApiFactory factory) => _factory = factory;

    [Fact]
    public void ScheduledDryRun_ShouldMatchRetentionOptions_WhenHostConfiguredFalse()
    {
        using var configured = _factory.WithWebHostBuilder(b => b.UseSetting(RetentionPurgeModeConfiguration.Key, "false"));

        var mode = configured.Services.GetRequiredService<IRetentionPurgeMode>();
        var options = configured.Services.GetRequiredService<IOptions<RetentionOptions>>();

        mode.ScheduledDryRun.Should().BeFalse();
        options.Value.DryRun.Should().BeFalse();
    }

    [Fact]
    public void HostStart_ShouldFailNamingKey_WhenValueIsNotBoolean()
    {
        using var configured = _factory.WithWebHostBuilder(b => b.UseSetting(RetentionPurgeModeConfiguration.Key, "flase"));

        var act = () => configured.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage("*Retention:DryRun*flase*");
    }

    [Fact]
    public void ScheduledDryRun_ShouldBeTrue_WhenKeyAbsent()
    {
        var mode = _factory.Services.GetRequiredService<IRetentionPurgeMode>();
        var options = _factory.Services.GetRequiredService<IOptions<RetentionOptions>>();

        mode.ScheduledDryRun.Should().BeTrue();
        options.Value.DryRun.Should().BeTrue();
    }

    [Fact]
    public void ScheduledDryRun_ShouldReadOptionsValue_WhenConstructed()
    {
        var mode = new RetentionPurgeMode(Options.Create(new RetentionOptions { DryRun = false }));

        mode.ScheduledDryRun.Should().BeFalse();
    }

    [Fact]
    public void LogStartupMode_ShouldLogOneInformationEntryNamingKey_WhenDry()
    {
        var logger = new CapturingLogger();

        RetentionPurgeModeLog.LogStartupMode(logger, dryRun: true);

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries.Single();
        level.Should().Be(LogLevel.Information);
        message.Should().Contain("dry-run").And.Contain("Retention:DryRun");
    }

    [Fact]
    public void LogStartupMode_ShouldLogOneWarningEntryNamingKey_WhenLive()
    {
        var logger = new CapturingLogger();

        RetentionPurgeModeLog.LogStartupMode(logger, dryRun: false);

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries.Single();
        level.Should().Be(LogLevel.Warning);
        message.Should().Contain("deletes expired rows").And.Contain("Retention:DryRun=false");
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
