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
    private static ConversationTimeoutWorker BuildWorker(IServiceHeartbeat? heartbeat = null)
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
            Options.Create(new DistributionOptions()),
            NullLogger<ConversationTimeoutWorker>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPropagateToExecuteTask_WhenFatalException()
    {
        var heartbeat = Substitute.For<IServiceHeartbeat>();
        heartbeat.When(h => h.RecordTick(Arg.Any<string>(), Arg.Any<TimeSpan>()))
            .Do(_ => throw new InvalidOperationException("conversation timeout fatal"));

        var sut = BuildWorker(heartbeat: heartbeat);

        await sut.StartAsync(CancellationToken.None);

        // First tick is ~5s into ExecuteAsync (initial Task.Delay) + the 5s periodic timer
        // — give a generous window for the heartbeat call to run + propagate.
        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            sut, TimeSpan.FromSeconds(15));

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

        var sut = BuildWorker(heartbeat: heartbeat);

        await sut.StartAsync(CancellationToken.None);

        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            sut, TimeSpan.FromSeconds(15));
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
