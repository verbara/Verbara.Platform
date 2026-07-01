using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Behaviour-preservation tests (C3): the worker timing intervals were made
/// options-overridable so tests can shrink them to milliseconds. These tests lock the
/// production defaults so the refactor stays byte-identical to the previous hardcoded
/// cadence (<c>ConversationTimeoutWorker</c> 5s startup + 5s sweep,
/// <c>QueueDistributionWorker</c> 3s warm-up).
/// </summary>
public sealed class DistributionOptionsDefaultsTests
{
    [Fact]
    public void ConversationTimeoutStartupDelayMs_ShouldDefaultTo5000_WhenUnset()
    {
        var options = new DistributionOptions();

        options.ConversationTimeoutStartupDelayMs.Should().Be(5000);
        TimeSpan.FromMilliseconds(options.ConversationTimeoutStartupDelayMs)
            .Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ConversationTimeoutSweepIntervalMs_ShouldDefaultTo5000_WhenUnset()
    {
        var options = new DistributionOptions();

        options.ConversationTimeoutSweepIntervalMs.Should().Be(5000);
        TimeSpan.FromMilliseconds(options.ConversationTimeoutSweepIntervalMs)
            .Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void QueueDistributionStartupDelayMs_ShouldDefaultTo3000_WhenUnset()
    {
        var options = new DistributionOptions();

        options.QueueDistributionStartupDelayMs.Should().Be(3000);
        TimeSpan.FromMilliseconds(options.QueueDistributionStartupDelayMs)
            .Should().Be(TimeSpan.FromSeconds(3));
    }

    // ─── Autonomous AI disposition (ADR-0034) — defaults are load-bearing safety ──────
    // AutonomousDispositionEnabled MUST default false (the global breaker / byte-identical
    // close path); the cap and correction window lock the documented production defaults.

    [Fact]
    public void AutonomousDispositionEnabled_ShouldDefaultToFalse_WhenUnset()
    {
        var options = new DistributionOptions();

        options.AutonomousDispositionEnabled.Should().BeFalse();
    }

    [Fact]
    public void AutonomousDispositionPerCycleCap_ShouldDefaultTo50_WhenUnset()
    {
        var options = new DistributionOptions();

        options.AutonomousDispositionPerCycleCap.Should().Be(50);
    }

    [Fact]
    public void AutonomousCorrectionWindowDays_ShouldDefaultTo30_WhenUnset()
    {
        var options = new DistributionOptions();

        options.AutonomousCorrectionWindowDays.Should().Be(30);
    }
}
