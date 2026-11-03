using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// conversations-close-after-inactivity task 2.7 (design D13; spec <c>conversation-inactivity-close</c>, "A
/// customer-returned close is counted"): <see cref="Verbara.Platform.Api.Services.CustomerReturnAnnouncer"/> adds one to
/// <c>conversations.system_closed{reason=customer-returned}</c> for a return close this delivery won, and nothing for one
/// it lost.
/// </summary>
public sealed class CustomerReturnAnnouncerCounterTests
{
    private static WebhookResult Delivery(params InboundMessage[] messages) => WebhookResult.From(messages, []);

    [Fact]
    public async Task AnnouncerCounter_ShouldCountOneCustomerReturned_WhenTheDeliveryWinsTheReturnClose()
    {
        using var h = new CustomerReturnHarness();
        await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(3));

        await h.CreateProcessor().ProcessAsync(h.Tenant, h.Channel, Delivery(h.Inbound("wamid.count-won")), CancellationToken.None);

        h.Store.ReturnCloseWins.Should().Be(1);
        h.CustomerReturnedCount.Should().Be(1);
    }

    [Fact]
    public async Task AnnouncerCounter_ShouldCountNothing_WhenTheDeliveryLosesTheReturnClose()
    {
        using var h = new CustomerReturnHarness();
        var resolved = await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(3));
        // Another writer closes the Resolved row after the pipeline read it and before its conditional close.
        h.Store.BeforeCloseResolved = async id =>
        {
            var row = (await h.Inner.GetByIdAsync(h.Tenant, id, CancellationToken.None))!;
            row.State = ConversationState.Closed;
            row.ClosedAt = h.Now;
            await h.Inner.SaveAsync(row, CancellationToken.None);
        };

        await h.CreateProcessor().ProcessAsync(h.Tenant, h.Channel, Delivery(h.Inbound("wamid.count-lost")), CancellationToken.None);

        (await h.Inner.GetByIdAsync(h.Tenant, resolved.ConversationId, CancellationToken.None))!.State
            .Should().Be(ConversationState.Closed);
        h.Store.ReturnCloseWins.Should().Be(0, "the other writer's close won");
        h.ReturnCloseEvents.Should().BeEmpty();
        h.CustomerReturnedCount.Should().Be(0);
    }
}
