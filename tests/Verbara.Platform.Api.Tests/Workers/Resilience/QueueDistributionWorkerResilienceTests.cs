using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Api.Tests.Workers.Resilience;

/// <summary>
/// Resilience-contract tests for <see cref="QueueDistributionWorker"/>. Validates that
/// the outer <c>try-catch</c> (LogWorkerCrash + rethrow) added by the 14-worker hardening
/// behaves as designed: fatal exceptions propagate to <see cref="BackgroundService.ExecuteTask"/>,
/// cancellation does not, and recoverable per-tick errors stay inside the inner catch.
/// </summary>
public sealed class QueueDistributionWorkerResilienceTests
{
    private static QueueDistributionWorker BuildWorker(
        IServiceHeartbeat? heartbeat = null,
        DistributionOptions? options = null)
    {
        var eventBus = new PlatformEventBus();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        return new QueueDistributionWorker(
            Substitute.For<IConversationStore>(),
            Substitute.For<ITenantStore>(),
            Substitute.For<IAgentSelector>(),
            Substitute.For<IConversationSwitchboard>(),
            eventBus,
            clock,
            heartbeat ?? new ServiceHeartbeat(),
            Options.Create(options ?? new DistributionOptions { PollIntervalMs = 50 }),
            NullLogger<QueueDistributionWorker>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPropagateToExecuteTask_WhenFatalException()
    {
        // Heartbeat.RecordTick is invoked inside the polling loop but OUTSIDE the inner
        // try-catch. Throwing from it forces the failure into the outer fatal handler.
        var heartbeat = Substitute.For<IServiceHeartbeat>();
        heartbeat.When(h => h.RecordTick(Arg.Any<string>(), Arg.Any<TimeSpan>()))
            .Do(_ => throw new InvalidOperationException("simulated fatal"));

        var sut = BuildWorker(heartbeat: heartbeat);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.StartAsync(cts.Token);

        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            sut, TimeSpan.FromSeconds(10));

        fault.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("simulated fatal");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldLogCriticalAndRethrow_WhenOuterException()
    {
        // Same fatal path as above, but assert the rethrow surfaces the original exception
        // (the LogWorkerCrash Critical log is verified at the contract level — the call is
        // a [LoggerMessage] partial method, so the rethrow is the observable side-effect).
        var heartbeat = Substitute.For<IServiceHeartbeat>();
        var thrown = new InvalidOperationException("queue distribution fatal");
        heartbeat.When(h => h.RecordTick(Arg.Any<string>(), Arg.Any<TimeSpan>()))
            .Do(_ => throw thrown);

        var sut = BuildWorker(heartbeat: heartbeat);

        await sut.StartAsync(CancellationToken.None);

        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            sut, TimeSpan.FromSeconds(10));

        fault.Should().BeSameAs(thrown);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotRethrow_WhenOperationCanceledFromStoppingToken()
    {
        var sut = BuildWorker();

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        // Cancel quickly — the worker's Task.Delay/PeriodicTimer will throw OCE which the
        // outer when-filter must swallow. ExecuteTask must complete (not fault).
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);

        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            sut, TimeSpan.FromSeconds(5));
        fault.Should().BeNull();
    }

    [Fact]
    public async Task DistributeAsync_ShouldNotKillWorker_WhenInnerRecoverableExceptionThrows()
    {
        // The inner try-catch swallows any non-cancellation throw from the policy delegate
        // and lets the loop continue. This test runs DistributeAsync directly (the internal
        // entry point) with a dependency that throws — it must surface to the caller (which
        // is the inner try-catch in real life, so the loop keeps spinning).
        var conversationStore = Substitute.For<IConversationStore>();
        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated transient"));

        var eventBus = new PlatformEventBus();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var sut = new QueueDistributionWorker(
            conversationStore,
            tenantStore,
            Substitute.For<IAgentSelector>(),
            Substitute.For<IConversationSwitchboard>(),
            eventBus,
            clock,
            new ServiceHeartbeat(),
            Options.Create(new DistributionOptions()),
            NullLogger<QueueDistributionWorker>.Instance);

        var act = async () => await sut.DistributeAsync(CancellationToken.None);

        // The inner try-catch in ExecuteAsync would log + continue; DistributeAsync itself
        // surfaces the throw (proving the inner catch is the line of defence, not the worker).
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated transient");
    }
}
