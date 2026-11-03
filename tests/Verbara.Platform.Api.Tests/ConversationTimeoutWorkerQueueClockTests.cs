using System.Globalization;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// conversations-close-after-inactivity task 2.5 (design D10; spec <c>conversation-queue-timeout</c>): the queue
/// timeout counts from when the conversation last entered the queue (metadata <c>_queuedAt</c>), never from its
/// creation, and an expired offer does not restart the wait. Runs over the real in-memory store and the real
/// switchboard (the <see cref="InactivityHarness"/>), with the inactivity pass switched off.
/// </summary>
public sealed partial class ConversationTimeoutWorkerTests
{
    private const int QueueTimeoutSeconds = 300;

    private static InactivityHarness NewQueueClockHarness()
    {
        var h = new InactivityHarness();
        h.Distribution.DefaultQueueTimeoutSeconds = QueueTimeoutSeconds;
        h.Distribution.OfferTimeoutSeconds = 30;
        h.Options.InactivityCloseEnabled = false;
        return h;
    }

    private static async Task<Conversation> SeedForQueueAsync(
        InactivityHarness h, ConversationState state, DateTimeOffset createdAt, ConversationOwner? owner = null)
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = h.Tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WhatsApp,
            Owner = owner,
            State = state,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastMessageAt = createdAt,
        };
        await h.Inner.SaveAsync(conv, CancellationToken.None);
        return conv;
    }

    // (a) The R2-offline failover re-queues an old conversation: it must wait its full queue timeout again.
    [Fact]
    public async Task QueueTimeout_ShouldKeepTheConversationQueued_WhenTheFailoverRequeuedAnOldConversation20SecondsAgo()
    {
        using var h = NewQueueClockHarness();
        var sweepAt = h.Now;
        var conv = await SeedForQueueAsync(
            h, ConversationState.Active, sweepAt.AddDays(-3), ConversationOwner.ForAgent(IdleAgent));

        h.Now = sweepAt.AddSeconds(-20);
        var requeued = await h.Switchboard.RequeueToFrontAsync(
            conv.ConversationId, h.Tenant, EntityId.From("q1"), CancellationToken.None);
        Assert.True(requeued.Success);

        h.Now = sweepAt;
        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Queued, (await h.GetAsync(conv)).State);
    }

    // (b) Entered the queue 301 s ago with a 300 s timeout: abandoned.
    [Fact]
    public async Task QueueTimeout_ShouldAbandon_WhenTheConversationEnteredTheQueue301SecondsAgo()
    {
        using var h = NewQueueClockHarness();
        var sweepAt = h.Now;
        var conv = await SeedForQueueAsync(h, ConversationState.Queued, sweepAt.AddSeconds(-301));

        h.Now = sweepAt.AddSeconds(-301);
        var assigned = await h.Switchboard.AssignToQueueAsync(
            conv.ConversationId, h.Tenant, EntityId.From("q1"), CancellationToken.None);
        Assert.True(assigned.Success);

        h.Now = sweepAt;
        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Abandoned, (await h.GetAsync(conv)).State);
    }

    // (c) Entered the queue 290 s before X, offered, the offer expired back to Queued at X - 5 s (by the worker's own
    // offer pass): the sweep at X + 15 s abandons it, because the expiry did not restart the wait.
    [Fact]
    public async Task QueueTimeout_ShouldAbandon_WhenAnExpiredOfferWouldOtherwiseHaveRestartedTheWait()
    {
        using var h = NewQueueClockHarness();
        var x = h.Now;
        var conv = await SeedForQueueAsync(h, ConversationState.Queued, x.AddSeconds(-290));

        h.Now = x.AddSeconds(-290);
        Assert.True((await h.Switchboard.AssignToQueueAsync(
            conv.ConversationId, h.Tenant, EntityId.From("q1"), CancellationToken.None)).Success);

        // The offer, as OfferToAgentAsync records it (state + offeredTo/offeredAt), made at X - 40 s.
        var offeredAt = x.AddSeconds(-40);
        var stored = await h.GetAsync(conv);
        stored.TransitionTo(ConversationState.Offered, offeredAt);
        stored.SetMetadata(ConversationOffer.OfferedToKey, "agent-slow");
        stored.SetMetadata(ConversationOffer.OfferedAtKey, offeredAt.ToString("O", CultureInfo.InvariantCulture));
        stored.UpdatedAt = offeredAt;
        await h.Inner.SaveAsync(stored, CancellationToken.None);

        // X - 5 s: the offer (35 s old, timeout 30 s) expires back to Queued; the queue has waited 285 s.
        h.Now = x.AddSeconds(-5);
        var worker = h.CreateWorker();
        await worker.ProcessTimeoutsAsync(CancellationToken.None);
        Assert.Equal(ConversationState.Queued, (await h.GetAsync(conv)).State);

        // X + 15 s: 305 s since it entered the queue, 20 s since the expiry.
        h.Now = x.AddSeconds(15);
        await worker.ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Abandoned, (await h.GetAsync(conv)).State);
    }

    // (d) Queued before the rule existed (no _queuedAt): counts from creation, as before.
    [Fact]
    public async Task QueueTimeout_ShouldAbandon_WhenNoQueueEntryTimeIsRecordedAndTheConversationWasCreated301SecondsAgo()
    {
        using var h = NewQueueClockHarness();
        var conv = await SeedForQueueAsync(
            h, ConversationState.Queued, h.Now.AddSeconds(-301), ConversationOwner.ForQueue(EntityId.From("q1")));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        var stored = await h.GetAsync(conv);
        Assert.Equal(ConversationState.Abandoned, stored.State);
        Assert.False(stored.Metadata.ContainsKey("_queuedAt"));
    }

    // (d') An unparseable _queuedAt falls back on creation too: created 301 s ago abandons, 299 s ago does not.
    [Theory]
    [InlineData(301, ConversationState.Abandoned)]
    [InlineData(299, ConversationState.Queued)]
    public async Task QueueTimeout_ShouldCountFromCreation_WhenTheQueueEntryTimeIsUnparseable(
        int createdSecondsAgo, ConversationState expected)
    {
        using var h = NewQueueClockHarness();
        var conv = await SeedForQueueAsync(
            h, ConversationState.Queued, h.Now.AddSeconds(-createdSecondsAgo), ConversationOwner.ForQueue(EntityId.From("q1")));
        var stored = await h.GetAsync(conv);
        stored.SetMetadata("_queuedAt", "not-a-timestamp");
        await h.Inner.SaveAsync(stored, CancellationToken.None);

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(expected, (await h.GetAsync(conv)).State);
    }
}
