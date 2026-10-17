using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Api.Tests.Webhooks;

/// <summary>
/// Builds real <see cref="WebhookDeliveryService"/> instances over the <see cref="WebhookDeliveryPostgresFixture"/>
/// stores (or any pair of stores, such as the InMemory ones), one shared <see cref="GatedReceiverHandler"/> and one
/// <see cref="MutableClock"/>. The services are never
/// started: tests drive the single-cycle seams (<c>DeliverForTestAsync</c> is the immediate path for one dequeued
/// item, <c>ProcessPendingRetriesOnceAsync</c> is one poll tick), per spec <c>test-determinism</c>. The transport
/// policy defaults to <see cref="ResiliencePolicy.NoOp"/> (the constructor's default), so each attempt sends exactly
/// one request.
/// </summary>
internal sealed class WebhookDeliveryHarness : IDisposable
{
    private readonly IWebhookDeliveryStore _deliveryStore;
    private readonly IWebhookSubscriptionStore _subscriptionStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<IDisposable> _owned = [];

    public WebhookDeliveryHarness(
        WebhookDeliveryPostgresFixture fixture, bool holdFirstRequest = true, Func<int, HttpResponseMessage>? answer = null)
        : this(fixture.DeliveryStore, fixture.SubscriptionStore, holdFirstRequest, answer)
    {
    }

    public WebhookDeliveryHarness(
        IWebhookDeliveryStore deliveryStore,
        IWebhookSubscriptionStore subscriptionStore,
        bool holdFirstRequest = true,
        Func<int, HttpResponseMessage>? answer = null)
    {
        _deliveryStore = deliveryStore;
        _subscriptionStore = subscriptionStore;
        Clock = new MutableClock(WebhookDeliveryPostgresFixture.Epoch);
        Receiver = new GatedReceiverHandler(holdFirstRequest, answer);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Logs));
        (EventBus, Dispatcher) = NewDispatcher();
    }

    public MutableClock Clock { get; }

    public GatedReceiverHandler Receiver { get; }

    /// <summary>Every record the services wrote at Information or above.</summary>
    public LogRecordCapture Logs { get; } = new();

    public IReadOnlyList<CapturedLogRecord> ErrorLogs =>
        [.. Logs.Records.Where(record => record.Level >= LogLevel.Error)];

    /// <summary>The bus of the first instance: publishing on it runs the real <see cref="WebhookDispatcher"/>.</summary>
    public PlatformEventBus EventBus { get; }

    /// <summary>The first instance's dispatcher; its <c>DeliveryReader</c> is the immediate path's channel.</summary>
    public WebhookDispatcher Dispatcher { get; }

    /// <summary>A delivery service that shares <see cref="Dispatcher"/> (the first instance).</summary>
    public WebhookDeliveryService CreateService(ResiliencePolicy? policy = null) => NewService(Dispatcher, policy);

    /// <summary>
    /// Another instance (a replica): its own dispatcher on its own bus, the same database, receiver and clock.
    /// </summary>
    public WebhookDeliveryService CreateReplica(ResiliencePolicy? policy = null)
    {
        var (_, dispatcher) = NewDispatcher();
        return NewService(dispatcher, policy);
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
        _loggerFactory.Dispose();
        Logs.Dispose();
        Receiver.Dispose();
    }

    private (PlatformEventBus Bus, WebhookDispatcher Dispatcher) NewDispatcher()
    {
        var bus = new PlatformEventBus();
        _owned.Add(bus);
        var dispatcher = new WebhookDispatcher(
            bus, _subscriptionStore, _deliveryStore, Clock, NullLogger<WebhookDispatcher>.Instance,
            MetricsWebhooksLeader.Holding());
        _owned.Add(dispatcher);
        return (bus, dispatcher);
    }

    private WebhookDeliveryService NewService(WebhookDispatcher dispatcher, ResiliencePolicy? policy) => new(
        dispatcher,
        _deliveryStore,
        _subscriptionStore,
        new ReceiverHttpClientFactory(Receiver),
        new CircuitBreakerPolicy(Options.Create(new CircuitBreakerOptions())),
        Clock,
        _loggerFactory.CreateLogger<WebhookDeliveryService>(),
        policy);

    private sealed class ReceiverHttpClientFactory(HttpMessageHandler receiver) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(receiver, disposeHandler: false);
    }
}
