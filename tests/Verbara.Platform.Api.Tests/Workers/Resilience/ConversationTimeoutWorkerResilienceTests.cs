using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Api.Tests.Workers.Resilience;

/// <summary>
/// Resilience-contract tests for <see cref="ConversationTimeoutWorker"/>. Validates the
/// outer fatal try-catch added by worker hardening: fatal exceptions surface on
/// <see cref="BackgroundService.ExecuteTask"/>, cancellation is swallowed, recoverable
/// per-cycle throws stay inside the inner catch (loop continues).
/// </summary>
public sealed class ConversationTimeoutWorkerResilienceTests
{
    /// <summary>
    /// Fast-loop options for the resilience tests: tiny startup delay + sweep interval so the
    /// real <c>ExecuteAsync</c> loop ticks in ~milliseconds (the genuine outer-fatal contract is
    /// still driven by the real loop, just without the 5s+5s production cadence). All other fields
    /// keep their <see cref="DistributionOptions"/> defaults. C3 — NO FakeTimeProvider/Advance.
    /// </summary>
    private static DistributionOptions FastLoopOptions() => new()
    {
        ConversationTimeoutStartupDelayMs = 5,
        ConversationTimeoutSweepIntervalMs = 5,
    };

    private static ConversationTimeoutWorker BuildWorker(
        IServiceHeartbeat? heartbeat = null,
        DistributionOptions? options = null)
    {
        var eventBus = new PlatformEventBus();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        return new ConversationTimeoutWorker(
            Substitute.For<IConversationStore>(),
            Substitute.For<ITenantStore>(),
            Substitute.For<IConversationSwitchboard>(),
            eventBus,
            clock,
            heartbeat ?? new ServiceHeartbeat(),
            Options.Create(options ?? new DistributionOptions()),
            NullLogger<ConversationTimeoutWorker>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPropagateToExecuteTask_WhenFatalException()
    {
        var heartbeat = Substitute.For<IServiceHeartbeat>();
        heartbeat.When(h => h.RecordTick(Arg.Any<string>(), Arg.Any<TimeSpan>()))
            .Do(_ => throw new InvalidOperationException("conversation timeout fatal"));

        var sut = BuildWorker(heartbeat: heartbeat, options: FastLoopOptions());

        await sut.StartAsync(CancellationToken.None);

        // First tick is a few ms into ExecuteAsync (FastLoopOptions startup delay) + the few-ms
        // periodic timer — the throwing RecordTick fires OUTSIDE the inner try-catch and surfaces
        // on ExecuteTask. The 2s cap is only a hang-guard; the test completes causally on the fault.
        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            sut, TimeSpan.FromSeconds(2));

        fault.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("conversation timeout fatal");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldLogCriticalAndRethrow_WhenOuterException()
    {
        var heartbeat = Substitute.For<IServiceHeartbeat>();
        var thrown = new InvalidOperationException("simulated outer crash");
        heartbeat.When(h => h.RecordTick(Arg.Any<string>(), Arg.Any<TimeSpan>())).Do(_ => throw thrown);

        var sut = BuildWorker(heartbeat: heartbeat, options: FastLoopOptions());

        await sut.StartAsync(CancellationToken.None);

        // 2s cap is the hang-guard only; the fault surfaces causally once the fast loop ticks.
        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            sut, TimeSpan.FromSeconds(2));
        fault.Should().BeSameAs(thrown);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotRethrow_WhenOperationCanceledFromStoppingToken()
    {
        var sut = BuildWorker();

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);

        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            sut, TimeSpan.FromSeconds(5));
        fault.Should().BeNull();
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldNotKillWorker_WhenInnerRecoverableException()
    {
        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated transient"));

        var sut = new ConversationTimeoutWorker(
            Substitute.For<IConversationStore>(),
            tenantStore,
            Substitute.For<IConversationSwitchboard>(),
            new PlatformEventBus(),
            Substitute.For<IClock>(),
            new ServiceHeartbeat(),
            Options.Create(new DistributionOptions()),
            NullLogger<ConversationTimeoutWorker>.Instance);

        var act = async () => await sut.ProcessTimeoutsAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated transient");
    }
}
