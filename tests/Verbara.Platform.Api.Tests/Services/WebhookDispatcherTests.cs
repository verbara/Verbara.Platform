using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Services;

public class WebhookDispatcherTests : IDisposable
{
    private readonly PlatformEventBus _eventBus = new();
    private readonly IWebhookSubscriptionStore _subStore = Substitute.For<IWebhookSubscriptionStore>();
    private readonly IWebhookDeliveryStore _deliveryStore = Substitute.For<IWebhookDeliveryStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly WebhookDispatcher _dispatcher;

    public WebhookDispatcherTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _dispatcher = new WebhookDispatcher(
            _eventBus, _subStore, _deliveryStore, _clock,
            NullLogger<WebhookDispatcher>.Instance);
    }

    [Fact]
    public async Task OnEvent_ShouldCreateDelivery_WhenMatchingSubscriptionExists()
    {
        var sub = new WebhookSubscription("s1", "t1", "Test", "https://example.com/hook",
            "secret", ["conversation.message"], true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _subStore.GetActiveByEventTypeAsync("t1", "conversation.message", Arg.Any<CancellationToken>())
            .Returns([sub]);

        _eventBus.Publish(new ConversationMessageEvent("t1", "c1", "m1", "user", "hello"));

        // Allow async subscription to process
        await Task.Delay(200);

        await _deliveryStore.Received(1).SaveAsync(
            Arg.Is<WebhookDelivery>(d =>
                d.SubscriptionId == "s1" &&
                d.EventType == "conversation.message" &&
                d.Status == WebhookDeliveryStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnEvent_ShouldNotCreateDelivery_WhenNoMatchingSubscription()
    {
        _subStore.GetActiveByEventTypeAsync("t1", "conversation.message", Arg.Any<CancellationToken>())
            .Returns(new List<WebhookSubscription>());

        _eventBus.Publish(new ConversationMessageEvent("t1", "c1", "m1", "user", "hello"));

        await Task.Delay(200);

        await _deliveryStore.DidNotReceive().SaveAsync(
            Arg.Any<WebhookDelivery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnEvent_ShouldCreateMultipleDeliveries_WhenMultipleSubscriptionsMatch()
    {
        var sub1 = new WebhookSubscription("s1", "t1", "Hook 1", "https://a.com/hook",
            "secret1", ["conversation.message"], true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var sub2 = new WebhookSubscription("s2", "t1", "Hook 2", "https://b.com/hook",
            "secret2", ["conversation.message"], true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _subStore.GetActiveByEventTypeAsync("t1", "conversation.message", Arg.Any<CancellationToken>())
            .Returns(new List<WebhookSubscription> { sub1, sub2 });

        _eventBus.Publish(new ConversationMessageEvent("t1", "c1", "m1", "user", "hello"));

        await Task.Delay(200);

        await _deliveryStore.Received(2).SaveAsync(
            Arg.Any<WebhookDelivery>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        _eventBus.Dispose();
        GC.SuppressFinalize(this);
    }
}
