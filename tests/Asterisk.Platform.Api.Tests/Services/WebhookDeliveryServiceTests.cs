using System.Net;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;
using Asterisk.Sdk.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Services;

public class WebhookDeliveryServiceTests
{
    private static readonly string[] EventTypes = ["event.fired"];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 60)]
    [InlineData(2, 300)]
    [InlineData(3, 1800)]
    [InlineData(4, 7200)]
    [InlineData(5, 18000)]
    [InlineData(6, 28800)]
    [InlineData(7, 28800)]
    [InlineData(100, 28800)] // Beyond array bounds clamps to last
    public void GetBackoffSeconds_ShouldReturnExpectedDelay(int attempt, int expectedSeconds)
    {
        WebhookDeliveryService.GetBackoffSeconds(attempt).Should().Be(expectedSeconds);
    }

    [Fact]
    public void BackoffSchedule_ShouldTotalApproximately24Hours()
    {
        var totalSeconds = 0;
        for (int i = 0; i < 8; i++)
            totalSeconds += WebhookDeliveryService.GetBackoffSeconds(i);

        // Total should be ~84660 seconds (~23.5 hours)
        totalSeconds.Should().BeInRange(80000, 90000);
    }

    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        // Keyed singleton registered in Program.cs depends on this exact string.
        WebhookDeliveryService.ResiliencePolicyKey.Should().Be("webhook.delivery");
    }

    [Fact]
    public async Task DeliverForTestAsync_ShouldInvokeHttpSendOnce_WhenDeliverySucceeds()
    {
        // Happy path: subscription exists, circuit closed, HTTP returns 200.
        // The wrapped ResiliencePolicy must invoke the inner send exactly once.
        var (service, handler, deliveryStore, delivery) = BuildSut(
            responseStatus: HttpStatusCode.OK,
            failFirstAttempt: false);

        await service.DeliverForTestAsync(delivery, CancellationToken.None);

        handler.Attempts.Should().Be(1);
        await deliveryStore.Received(1).UpdateAsync(
            Arg.Is<WebhookDelivery>(d => d.Status == WebhookDeliveryStatus.Delivered),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeliverForTestAsync_ShouldRetryAndSucceed_WhenFirstHttpAttemptThrowsTransientError()
    {
        // First HTTP call throws HttpRequestException (transient blip).
        // Policy is configured with retry=2/1ms, so second attempt succeeds with 200.
        var (service, handler, deliveryStore, delivery) = BuildSut(
            responseStatus: HttpStatusCode.OK,
            failFirstAttempt: true);

        await service.DeliverForTestAsync(delivery, CancellationToken.None);

        handler.Attempts.Should().Be(2, "first attempt threw, policy retried and succeeded");
        await deliveryStore.Received(1).UpdateAsync(
            Arg.Is<WebhookDelivery>(d => d.Status == WebhookDeliveryStatus.Delivered),
            Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (WebhookDeliveryService service,
                    CountingHttpHandler handler,
                    IWebhookDeliveryStore deliveryStore,
                    WebhookDelivery delivery) BuildSut(
        HttpStatusCode responseStatus, bool failFirstAttempt)
    {
        var handler = new CountingHttpHandler(responseStatus, failFirstAttempt ? 1 : 0);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        var sub = new WebhookSubscription(
            SubscriptionId: "sub-1",
            TenantId: "tenant-1",
            Name: "Test",
            EndpointUrl: "https://example.com/hook",
            Secret: "secret",
            EventTypes: EventTypes,
            IsActive: true,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var subStore = Substitute.For<IWebhookSubscriptionStore>();
        subStore.GetByIdAsync("sub-1", Arg.Any<CancellationToken>()).Returns(sub);

        var deliveryStore = Substitute.For<IWebhookDeliveryStore>();

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var circuitOptions = Options.Create(new CircuitBreakerOptions
        {
            FailureThreshold = 5,
            CooldownSeconds = 30,
            MaxCooldownSeconds = 300,
            CooldownMultiplier = 2.0,
        });
        var circuitBreaker = new CircuitBreakerPolicy(circuitOptions);

        // DeliverForTestAsync bypasses the channel/dispatcher loop; we only need a non-null
        // dispatcher so the constructor is satisfied.
        var eventBus = new PlatformEventBus();
        var dispatcher = new WebhookDispatcher(
            eventBus,
            subStore,
            deliveryStore,
            clock,
            NullLogger<WebhookDispatcher>.Instance);

        // Real ResiliencePolicy — retry once with negligible backoff so tests stay fast.
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();

        var service = new WebhookDeliveryService(
            dispatcher,
            deliveryStore,
            subStore,
            httpClientFactory,
            circuitBreaker,
            clock,
            NullLogger<WebhookDeliveryService>.Instance,
            policy);

        var delivery = new WebhookDelivery(
            DeliveryId: "delivery-1",
            TenantId: "tenant-1",
            SubscriptionId: "sub-1",
            EventType: "event.fired",
            Payload: """{"hello":"world"}""",
            Status: WebhookDeliveryStatus.Pending,
            Attempts: 0,
            MaxAttempts: 8,
            NextRetryAt: null,
            LastResponseCode: null,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null);

        return (service, handler, deliveryStore, delivery);
    }

    /// <summary>
    /// HTTP handler that optionally throws on the first N requests (simulating a transient
    /// failure) and then returns <paramref name="responseStatus"/> for subsequent requests.
    /// </summary>
    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _responseStatus;
        private readonly int _failFirstCount;
        private int _attempts;

        public CountingHttpHandler(HttpStatusCode responseStatus, int failFirstCount)
        {
            _responseStatus = responseStatus;
            _failFirstCount = failFirstCount;
        }

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= _failFirstCount)
                throw new HttpRequestException($"transient failure (attempt {attempt})");

            return Task.FromResult(new HttpResponseMessage(_responseStatus));
        }
    }
}
