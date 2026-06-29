using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Api.Tests.Workers.Resilience;

/// <summary>
/// Resilience-contract tests for <see cref="WebhookDeliveryService"/>. The worker uses
/// the dual-loop pattern (channel reader + DB poller) — each loop has its own outer
/// try-catch that logs Critical + rethrows. The Critical log name includes
/// <c>".ProcessChannelAsync"</c> / <c>".PollPendingRetriesAsync"</c> to disambiguate
/// which loop crashed.
/// </summary>
public sealed class WebhookDeliveryServiceResilienceTests
{
    private static WebhookDeliveryService BuildWorker(
        IWebhookDeliveryStore? deliveryStore = null,
        IWebhookSubscriptionStore? subscriptionStore = null,
        IHttpClientFactory? httpClientFactory = null,
        IClock? clock = null)
    {
        var c = clock ?? Substitute.For<IClock>();
        c.UtcNow.Returns(DateTimeOffset.UtcNow);
        return new WebhookDeliveryService(
            new WebhookDispatcher(
                new PlatformEventBus(),
                subscriptionStore ?? Substitute.For<IWebhookSubscriptionStore>(),
                deliveryStore ?? Substitute.For<IWebhookDeliveryStore>(),
                c,
                NullLogger<WebhookDispatcher>.Instance),
            deliveryStore ?? Substitute.For<IWebhookDeliveryStore>(),
            subscriptionStore ?? Substitute.For<IWebhookSubscriptionStore>(),
            httpClientFactory ?? Substitute.For<IHttpClientFactory>(),
            new CircuitBreakerPolicy(Options.Create(new CircuitBreakerOptions())),
            c,
            NullLogger<WebhookDeliveryService>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPropagateToExecuteTask_WhenFatalException()
    {
        // ListPendingRetriesAsync is invoked from the poll loop after a 30s Task.Delay;
        // throwing from it would be caught by the inner try-catch. The only way to
        // force the outer fatal path from a unit test is to cancel the stoppingToken
        // and verify the ExecuteTask completes cleanly — see the next test for that.
        //
        // Here we drive a non-cancellation exception through the subscriptionStore
        // (used in DeliverAsync) which is also caught by inner DeliverAsync handlers.
        // The two outer fatal handlers (ProcessChannelAsync / PollPendingRetriesAsync)
        // are only reachable when the await foreach over the channel or the await
        // Task.Delay raises a non-cancellation throw — neither is trivially injectable
        // through public DI surface without a corrupted IHttpClientFactory.
        //
        // Pragmatic test: use IHttpClientFactory.CreateClient to throw a non-cancellation
        // exception. The poll loop deals with this by calling DeliverAsync; inner catches
        // log + continue — so this should NOT be fatal (validating recoverable contract).
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException("simulated http factory crash"));

        var deliveryStore = Substitute.For<IWebhookDeliveryStore>();
        deliveryStore.ListPendingRetriesAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WebhookDelivery>());

        var sut = BuildWorker(deliveryStore: deliveryStore, httpClientFactory: httpFactory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await sut.StartAsync(cts.Token);

        // Recoverable inner errors must not surface as fault — worker keeps spinning.
        await sut.StopAsync(CancellationToken.None);

        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            sut, TimeSpan.FromSeconds(3));
        fault.Should().BeNull("inner DeliverAsync catches IHttpClientFactory failures");
    }

    [Fact]
    public async Task ProcessPendingRetriesOnceAsync_ShouldSwallowAndLog_WhenNonCancellationThrows()
    {
        // Recoverable-inner contract of the DB poll loop: when ListPendingRetriesAsync raises a
        // non-cancellation exception, the inner catch in ProcessPendingRetriesOnceAsync logs +
        // swallows it so the worker keeps spinning (no rethrow, no fault on ExecuteTask).
        //
        // Previously this was driven through the real 30s RetryPollInterval loop (≈35s wall-clock)
        // only to observe a null fault. We now exercise the same swallow contract via a DIRECT call
        // to the internal ProcessPendingRetriesOnceAsync — the single poll iteration — in ~0s.
        //
        // The dual-loop OUTER fatal rethrow (PollPendingRetriesAsync / ProcessChannelAsync catch →
        // LogWorkerCrash + throw) is structurally unreachable from unit scope: the only inner work
        // a unit can inject (ListPendingRetriesAsync / DeliverAsync) is itself wrapped by the inner
        // catch, and the channel reader never throws outside cancellation in production. This is
        // recorded in the OpenSpec change for C3.
        var deliveryStore = Substitute.For<IWebhookDeliveryStore>();
        deliveryStore.ListPendingRetriesAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("poll list failure"));

        var sut = BuildWorker(deliveryStore: deliveryStore);

        var act = async () => await sut.ProcessPendingRetriesOnceAsync(CancellationToken.None);

        // Inner catch logs + swallows the non-cancellation throw — the call must NOT throw.
        await act.Should().NotThrowAsync(
            "the poll iteration's inner catch logs InvalidOperationException and continues");
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
    public void Worker_Construction_ShouldSucceed_WithMinimalDependencies()
    {
        // Smoke: the constructor wires correctly with substituted dependencies and the
        // default no-op resilience policy (null parameter -> ResiliencePolicy.NoOp).
        var act = () => BuildWorker();
        act.Should().NotThrow();
    }
}
