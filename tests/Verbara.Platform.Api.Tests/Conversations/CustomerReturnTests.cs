using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// conversations-close-after-inactivity task 2.3 (design D7, D8; spec <c>conversation-customer-return</c>): the inbound
/// claim and the return to <c>Resolved</c>, through the real pipeline and its two callers, plus the audit round 4
/// residuals F1 (a failure after a won return close still announces it) and P3 (an announcer fault on the webhook's
/// duplicate branch is logged, never a non-2xx).
/// </summary>
public sealed class CustomerReturnTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(10);

    /// <summary>Which delivery of a race the current async flow is (set inside each <c>Task.Run</c>).</summary>
    private static readonly AsyncLocal<string?> Role = new();

    private static WebhookResult Delivery(params InboundMessage[] messages) => WebhookResult.From(messages, []);

    // ── (a) WhatsApp return ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_ShouldCloseTheResolvedConversationAndRouteANewOne_WhenTheCustomerReturnsOnWhatsApp()
    {
        using var h = new CustomerReturnHarness();
        var resolved = await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));

        await h.CreateProcessor().ProcessAsync(h.Tenant, h.Channel, Delivery(h.Inbound("wamid.ret-a")), CancellationToken.None);

        var closed = (await h.Inner.GetByIdAsync(h.Tenant, resolved.ConversationId, CancellationToken.None))!;
        closed.State.Should().Be(ConversationState.Closed);
        closed.ClosedAt.Should().Be(h.Now);
        closed.Metadata[ConversationCloseReasons.MetadataKey].Should().Be(ConversationCloseReasons.CustomerReturned);
        closed.Owner.Should().Be(ConversationOwner.ForAgent(CustomerReturnHarness.AgentA), "the owner is kept for decision 6");
        (await h.MessagesOfAsync(resolved.ConversationId)).Should().BeEmpty("the reply never lands in the Resolved row");
        var holding = (await h.ConversationsHoldingAsync("wamid.ret-a")).Should().ContainSingle().Subject;
        holding.ConversationId.Should().NotBe(resolved.ConversationId);
        h.AssignmentsOf(holding.ConversationId).Should().Be(1, "the new conversation is routed like a first message");
        h.ReturnCloseEvents.Should().ContainSingle();
        h.ReturnAuditCount.Should().Be(1);
        h.CustomerReturnedCount.Should().Be(1);
    }

    // ── (b) WebChat return ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteFirstInboundAsync_ShouldRouteANewConversationAndMoveTheSession_WhenTheVisitorReturnsToAResolvedChat()
    {
        using var h = new CustomerReturnHarness(ChannelType.WebChat);
        var resolved = await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(2));
        var sessionId = await h.ConnectSessionAsync(resolved.ConversationId);
        h.Sessions.TryMarkRouted(sessionId).Should().BeTrue("the session's first message was routed long ago");
        var inbound = h.Inbound("wc-ret-b");

        var result = await h.Announcer.ProcessAsync(h.Pipeline, inbound, h.Tenant, h.Channel, CancellationToken.None);
        await h.CreateWebChatRouter().RouteFirstInboundAsync(sessionId, h.Tenant, result, inbound.Content, CancellationToken.None);

        var closed = (await h.Inner.GetByIdAsync(h.Tenant, resolved.ConversationId, CancellationToken.None))!;
        closed.State.Should().Be(ConversationState.Closed);
        closed.Metadata[ConversationCloseReasons.MetadataKey].Should().Be(ConversationCloseReasons.CustomerReturned);
        var holding = (await h.ConversationsHoldingAsync("wc-ret-b")).Should().ContainSingle().Subject;
        holding.ConversationId.Should().NotBe(resolved.ConversationId);
        h.AssignmentsOf(holding.ConversationId).Should().Be(1, "an agent must be offered the returning visitor");
        (await h.Sessions.GetSessionAsync(sessionId))!.ConversationId.Should().Be(holding.ConversationId);
        h.ReturnCloseEvents.Should().ContainSingle();
        h.ReturnAuditCount.Should().Be(1);
        h.CustomerReturnedCount.Should().Be(1);
    }

    // ── (d) the inactivity close racing the inbound message ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_ShouldStoreTheReplyInANewRoutedConversation_WhenTheInactivityCloseCommitsAfterTheRead()
    {
        using var h = new CustomerReturnHarness();
        var idle = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(73));
        var first = 0;
        h.Store.AfterFindActive = async found =>
        {
            if (found?.ConversationId == idle.ConversationId && Interlocked.Exchange(ref first, 1) == 0)
            {
                (await h.Inner.TryCloseInactiveAsync(h.Tenant, InactivityRequest(h, idle), CancellationToken.None))
                    .Should().NotBeNull("the sweep's close commits between the pipeline's read and its claim");
            }
        };

        await h.CreateProcessor().ProcessAsync(h.Tenant, h.Channel, Delivery(h.Inbound("wamid.race-d")), CancellationToken.None);

        (await h.Inner.GetByIdAsync(h.Tenant, idle.ConversationId, CancellationToken.None))!.State.Should().Be(ConversationState.Closed);
        (await h.MessagesOfAsync(idle.ConversationId)).Should().BeEmpty("the closed conversation holds no new message");
        var holding = (await h.ConversationsHoldingAsync("wamid.race-d")).Should().ContainSingle().Subject;
        holding.ConversationId.Should().NotBe(idle.ConversationId);
        h.AssignmentsOf(holding.ConversationId).Should().Be(1);
    }

    [Fact]
    public async Task ProcessAsync_ShouldKeepTheClaimedChatOpen_WhenTheInactivityCloseRunsAfterTheClaim()
    {
        using var h = new CustomerReturnHarness();
        var idle = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(73));
        InactivityCloseOutcome? lateClose = new("unset", default);
        h.MessageStore.BeforeInsert = async _ =>
            lateClose = await h.Inner.TryCloseInactiveAsync(h.Tenant, InactivityRequest(h, idle), CancellationToken.None);

        await h.CreateProcessor().ProcessAsync(h.Tenant, h.Channel, Delivery(h.Inbound("wamid.race-d2")), CancellationToken.None);

        lateClose.Should().BeNull("the claim advanced the clock, so the close's predicate no longer matches");
        (await h.Inner.GetByIdAsync(h.Tenant, idle.ConversationId, CancellationToken.None))!.State.Should().Be(ConversationState.Active);
        (await h.MessagesOfAsync(idle.ConversationId)).Should().ContainSingle(m => m.ExternalMessageId == "wamid.race-d2");
    }

    // ── (e) two different messages return at once ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_ShouldCloseOnceWithOneEventAndAudit_WhenTwoDifferentMessagesReturnAtOnce()
    {
        var (h, results) = await RunTwoDifferentReturnsAsync(unconditionalClose: false);
        using (h)
        {
            results.Count(r => r.ClosedOnReturnId is not null).Should().Be(1, "only the delivery that won the close carries it");
            h.Store.ReturnCloseWins.Should().Be(1);
            h.ReturnCloseEvents.Should().ContainSingle();
            h.ReturnAuditCount.Should().Be(1);
            h.CustomerReturnedCount.Should().Be(1);
        }
    }

    /// <summary>
    /// ADR-0030 widened-window control for (e): the same hold (both deliveries have read the <c>Resolved</c> row before
    /// either closes it) over a return close with no state condition. It double-closes, so the hold really opens the
    /// window the conditional close defends.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_ShouldDoubleClose_WhenTheReturnCloseIsUnconditional_Control()
    {
        var (h, results) = await RunTwoDifferentReturnsAsync(unconditionalClose: true);
        using (h)
        {
            results.Count(r => r.ClosedOnReturnId is not null).Should().Be(2);
            h.ReturnCloseEvents.Should().HaveCount(2);
            h.ReturnAuditCount.Should().Be(2);
        }
    }

    private static async Task<(CustomerReturnHarness H, IReadOnlyList<PipelineResult> Results)> RunTwoDifferentReturnsAsync(bool unconditionalClose)
    {
        var h = new CustomerReturnHarness();
        var resolved = await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));
        h.Store.UnconditionalReturnClose = unconditionalClose;
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;
        h.Store.AfterFindActive = async found =>
        {
            if (found?.ConversationId != resolved.ConversationId || found.State != ConversationState.Resolved)
                return;
            if (Interlocked.Increment(ref readers) == 2)
                bothRead.TrySetResult();
            await bothRead.Task.WaitAsync(Hold);
        };
        var recording = new RecordingPipeline(h.Pipeline);
        var processor = h.CreateProcessor(recording);

        await Task.WhenAll(
            Task.Run(() => processor.ProcessAsync(h.Tenant, h.Channel, Delivery(h.Inbound("wamid.e-1", text: "uno")), CancellationToken.None)),
            Task.Run(() => processor.ProcessAsync(h.Tenant, h.Channel, Delivery(h.Inbound("wamid.e-2", text: "dos")), CancellationToken.None)));

        (await h.MessagesOfAsync(resolved.ConversationId)).Should().BeEmpty("both messages are stored outside the Resolved row");
        (await h.ConversationsHoldingAsync("wamid.e-1")).Should().ContainSingle();
        (await h.ConversationsHoldingAsync("wamid.e-2")).Should().ContainSingle();
        return (h, recording.Results);
    }

    // ── (e2) a replay announces nothing ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_ShouldCarryNoClosedOnReturnIdAndAnnounceNothing_WhenTheReturningMessageIsReplayed()
    {
        using var h = new CustomerReturnHarness();
        await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));
        var recording = new RecordingPipeline(h.Pipeline);
        var processor = h.CreateProcessor(recording);
        var inbound = h.Inbound("wamid.replay");

        await processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None);
        await processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None);

        recording.Results.Should().HaveCount(2);
        recording.Results[0].ClosedOnReturnId.Should().NotBeNull();
        recording.Results[1].IsDuplicate.Should().BeTrue();
        recording.Results[1].ClosedOnReturnId.Should().BeNull("a replay never reaches the claim");
        h.ReturnCloseEvents.Should().ContainSingle();
        h.ReturnAuditCount.Should().Be(1);
        h.CustomerReturnedCount.Should().Be(1);
    }

    // ── (e5) identical deliveries: A wins the close, B wins the insert ─────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_ShouldAnnounceTheCloseOnceFromTheLostInsert_WhenIdenticalDeliveriesSplitCloseAndInsert()
    {
        using var h = new CustomerReturnHarness();
        var resolved = await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));
        var recording = new RecordingPipeline(h.Pipeline);
        var processor = h.CreateProcessor(recording);
        var inbound = h.Inbound("wamid.e5");
        ArmSplitCloseAndInsert(h, reuse: false);

        await RunAsAAndBAsync(
            () => processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None),
            () => processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None));

        AssertSplitResults(h, recording, resolved);
        var holding = (await h.ConversationsHoldingAsync("wamid.e5")).Should().ContainSingle().Subject;
        (await h.MessagesOfAsync(holding.ConversationId)).Should().ContainSingle("the message is stored once");
        h.AssignmentsOf(holding.ConversationId).Should().Be(1, "the insert winner routes it; the duplicate routes nothing");
    }

    [Fact]
    public async Task RouteFirstInboundAsync_ShouldAnnounceTheCloseOnceFromTheLostInsert_WhenIdenticalDeliveriesSplitCloseAndInsert()
    {
        using var h = new CustomerReturnHarness(ChannelType.WebChat);
        var resolved = await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));
        var sessionId = await h.ConnectSessionAsync(resolved.ConversationId);
        h.Sessions.TryMarkRouted(sessionId);
        var recording = new RecordingPipeline(h.Pipeline);
        var router = h.CreateWebChatRouter();
        var inbound = h.Inbound("wc-e5");
        ArmSplitCloseAndInsert(h, reuse: false);

        await RunAsAAndBAsync(
            () => ThroughWebChatAsync(h, recording, router, sessionId, inbound),
            () => ThroughWebChatAsync(h, recording, router, sessionId, inbound));

        AssertSplitResults(h, recording, resolved);
        var holding = (await h.ConversationsHoldingAsync("wc-e5")).Should().ContainSingle().Subject;
        h.AssignmentsOf(holding.ConversationId).Should().Be(1);
        (await h.Sessions.GetSessionAsync(sessionId))!.ConversationId.Should().Be(holding.ConversationId);
    }

    /// <summary>
    /// The reuse ordering (audit F4): A wins the close and creates the replacement R, then is held before its insert;
    /// B finds R (<c>Queued</c>, no owner), reports <c>IsNewConversation: false</c> and wins the insert. R is routed
    /// exactly once, by the insert winner.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_ShouldRouteTheReplacementOnce_WhenTheInsertWinnerReusedIt()
    {
        using var h = new CustomerReturnHarness();
        var resolved = await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));
        var recording = new RecordingPipeline(h.Pipeline);
        var processor = h.CreateProcessor(recording);
        var inbound = h.Inbound("wamid.e5-reuse");
        ArmSplitCloseAndInsert(h, reuse: true);

        await RunAsAAndBAsync(
            () => processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None),
            () => processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None));

        AssertSplitResults(h, recording, resolved);
        var r = (await h.ConversationsHoldingAsync("wamid.e5-reuse")).Should().ContainSingle().Subject;
        recording.Results.Should().OnlyContain(x => x.ConversationId == r.ConversationId && !x.IsNewConversation,
            "B reused A's replacement, and A's lost-insert branch kept it because a message landed there");
        h.AssignmentsOf(r.ConversationId).Should().Be(1);
    }

    [Fact]
    public async Task RouteFirstInboundAsync_ShouldRouteTheReplacementOnceAndMoveTheSession_WhenTheInsertWinnerReusedIt()
    {
        using var h = new CustomerReturnHarness(ChannelType.WebChat);
        var resolved = await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));
        var sessionId = await h.ConnectSessionAsync(resolved.ConversationId);
        h.Sessions.TryMarkRouted(sessionId).Should().BeTrue("an already-routed session");
        var recording = new RecordingPipeline(h.Pipeline);
        var router = h.CreateWebChatRouter();
        var inbound = h.Inbound("wc-e5-reuse");
        ArmSplitCloseAndInsert(h, reuse: true);

        await RunAsAAndBAsync(
            () => ThroughWebChatAsync(h, recording, router, sessionId, inbound),
            () => ThroughWebChatAsync(h, recording, router, sessionId, inbound));

        AssertSplitResults(h, recording, resolved);
        var r = (await h.ConversationsHoldingAsync("wc-e5-reuse")).Should().ContainSingle().Subject;
        recording.Results.Should().OnlyContain(x => !x.IsNewConversation);
        h.AssignmentsOf(r.ConversationId).Should().Be(1, "R is assigned exactly once, by the insert winner");
        (await h.Inner.GetByIdAsync(h.Tenant, r.ConversationId, CancellationToken.None))!.Owner
            .Should().Be(ConversationOwner.ForQueue(CustomerReturnHarness.QueueId));
        (await h.Sessions.GetSessionAsync(sessionId))!.ConversationId.Should().Be(r.ConversationId,
            "the visitor's session follows R");
    }

    /// <summary>
    /// ADR-0030 widened-window control for (e5): the same holds, with a copy of the pipeline result that drops the flag
    /// on every duplicate (the old D8 wording, "always null on a duplicate"). The won close is then never announced.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_ShouldAnnounceNothing_WhenTheFlagIsDroppedOnEveryDuplicate_Control()
    {
        using var h = new CustomerReturnHarness();
        await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));
        var recording = new RecordingPipeline(h.Pipeline) { DropFlagOnDuplicate = true };
        var processor = h.CreateProcessor(recording);
        var inbound = h.Inbound("wamid.e5-control");
        ArmSplitCloseAndInsert(h, reuse: false);

        await RunAsAAndBAsync(
            () => processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None),
            () => processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None));

        h.Store.ReturnCloseWins.Should().Be(1, "the close committed");
        h.ReturnCloseEvents.Should().BeEmpty();
        h.ReturnAuditCount.Should().Be(0);
        h.CustomerReturnedCount.Should().Be(0);
    }

    /// <summary>
    /// The holds of (e5). A is held before its close until B has read the <c>Resolved</c> row (both passed
    /// deduplication). Ordering 1 (<paramref name="reuse"/> false): A is held after its won close until B has inserted,
    /// and B's resolution is held until A has closed, so B opens its own conversation. Reuse ordering: B's resolution is
    /// held until A has created R and reached its insert, and A is held there until B has inserted into R.
    /// </summary>
    private static void ArmSplitCloseAndInsert(CustomerReturnHarness h, bool reuse)
    {
        var bRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aAtInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bInserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bFirstRead = 0;

        h.Store.BeforeCloseResolved = async _ =>
        {
            if (Role.Value == "A")
                await bRead.Task.WaitAsync(Hold);
        };
        h.Store.AfterFindActive = async found =>
        {
            if (Role.Value != "B" || Interlocked.Exchange(ref bFirstRead, 1) != 0)
                return;
            found?.State.Should().Be(ConversationState.Resolved, "B read the Resolved row before A closed it");
            bRead.TrySetResult();
            await (reuse ? aAtInsert.Task : aClosed.Task).WaitAsync(Hold);
        };
        h.Store.AfterCloseResolved = async (_, won) =>
        {
            if (Role.Value != "A")
                return;
            won.Should().BeTrue("A wins the close");
            aClosed.TrySetResult();
            if (!reuse)
                await bInserted.Task.WaitAsync(Hold);
        };
        h.MessageStore.BeforeInsert = async _ =>
        {
            if (Role.Value == "A" && reuse)
            {
                aAtInsert.TrySetResult();
                await bInserted.Task.WaitAsync(Hold);
            }
        };
        h.MessageStore.AfterInsert = (sent, stored) =>
        {
            if (Role.Value == "B")
            {
                stored.MessageId.Should().Be(sent.MessageId, "B wins the insert");
                bInserted.TrySetResult();
            }

            return Task.CompletedTask;
        };
    }

    private static void AssertSplitResults(CustomerReturnHarness h, RecordingPipeline recording, Conversation resolved)
    {
        var a = recording.Results.Should().ContainSingle(r => r.IsDuplicate, "A lost the insert").Subject;
        a.ClosedOnReturnId.Should().Be(resolved.ConversationId, "A won the close, so its lost-insert branch keeps the flag");
        var b = recording.Results.Should().ContainSingle(r => !r.IsDuplicate).Subject;
        b.ClosedOnReturnId.Should().BeNull("B lost the close");
        h.Store.ReturnCloseWins.Should().Be(1);
        h.ReturnCloseEvents.Should().ContainSingle();
        h.ReturnAuditCount.Should().Be(1);
        h.CustomerReturnedCount.Should().Be(1);
    }

    private static Task RunAsAAndBAsync(Func<Task> a, Func<Task> b) => Task.WhenAll(
        Task.Run(async () =>
        {
            Role.Value = "A";
            await a();
        }),
        Task.Run(async () =>
        {
            Role.Value = "B";
            await b();
        }));

    private static async Task ThroughWebChatAsync(
        CustomerReturnHarness h, RecordingPipeline pipeline, WebChatInboundRouter router, string sessionId, InboundMessage inbound)
    {
        var result = await h.Announcer.ProcessAsync(pipeline, inbound, h.Tenant, h.Channel, CancellationToken.None);
        await router.RouteFirstInboundAsync(sessionId, h.Tenant, result, inbound.Content, CancellationToken.None);
    }

    // ── (e4) the claim's clock is the platform's ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_ShouldSetTheClockToThePlatformTime_WhenTheProviderTimestampIsFiveDaysOld()
    {
        using var h = new CustomerReturnHarness();
        var active = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(10));

        await h.CreateProcessor().ProcessAsync(
            h.Tenant, h.Channel, Delivery(h.Inbound("wamid.late", providerTimestamp: h.Now.AddDays(-5))), CancellationToken.None);

        (await h.MessagesOfAsync(active.ConversationId)).Should().ContainSingle();
        (await h.Inner.GetByIdAsync(h.Tenant, active.ConversationId, CancellationToken.None))!.LastMessageAt.Should().Be(h.Now);
    }

    // ── (f) a reply keeps an idle chat open ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_ShouldKeepTheChatOpenThreeHoursLater_WhenTheCustomerRepliesToAChat70HoursIdle()
    {
        using var h = new CustomerReturnHarness();
        var active = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(70));

        await h.CreateProcessor().ProcessAsync(h.Tenant, h.Channel, Delivery(h.Inbound("wamid.f")), CancellationToken.None);
        h.Now += TimeSpan.FromHours(3);

        var idle = await h.Inner.ListInactiveAsync(
            h.Tenant, [ConversationState.Active], h.Now - TimeSpan.FromHours(72), 100, CancellationToken.None);
        idle.Should().NotContain(c => c.ConversationId == active.ConversationId);
        (await h.Inner.TryCloseInactiveAsync(h.Tenant, InactivityRequest(h, active), CancellationToken.None)).Should().BeNull();
        (await h.Inner.GetByIdAsync(h.Tenant, active.ConversationId, CancellationToken.None))!.State.Should().Be(ConversationState.Active);
    }

    // ── Audit round 4 F1: a failure after a won return close still announces it ────────────────────────────────

    [Fact]
    public async Task ProcessAsync_ShouldAnnounceTheReturnCloseAndStillFail_WhenTheInsertThrowsAfterTheCloseWon()
    {
        using var h = new CustomerReturnHarness();
        var resolved = await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));
        var faults = 1;
        h.MessageStore.InsertFault = _ =>
            Interlocked.Decrement(ref faults) >= 0 ? new InvalidOperationException("database unavailable") : null;
        var processor = h.CreateProcessor();
        var inbound = h.Inbound("wamid.f1");

        var act = () => processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>("the provider must retry a delivery that was not stored")
            .WithMessage("database unavailable");
        (await h.Inner.GetByIdAsync(h.Tenant, resolved.ConversationId, CancellationToken.None))!.State.Should().Be(ConversationState.Closed);
        h.ReturnCloseEvents.Should().ContainSingle("the committed close is announced although the insert failed");
        h.ReturnAuditCount.Should().Be(1);
        h.CustomerReturnedCount.Should().Be(1);

        // The provider's retry stores the message in a routed conversation and announces nothing more.
        await processor.ProcessAsync(h.Tenant, h.Channel, Delivery(inbound), CancellationToken.None);

        var holding = (await h.ConversationsHoldingAsync("wamid.f1")).Should().ContainSingle().Subject;
        holding.ConversationId.Should().NotBe(resolved.ConversationId);
        h.AssignmentsOf(holding.ConversationId).Should().Be(1);
        h.ReturnCloseEvents.Should().ContainSingle();
        h.ReturnAuditCount.Should().Be(1);
        h.CustomerReturnedCount.Should().Be(1);
    }

    [Fact]
    public async Task AnnouncerProcessAsync_ShouldAnnounceTheReturnCloseAndRethrow_WhenTheWebChatInsertThrowsAfterTheCloseWon()
    {
        using var h = new CustomerReturnHarness(ChannelType.WebChat);
        await h.SeedAsync(ConversationState.Resolved, idle: TimeSpan.FromDays(10));
        h.MessageStore.InsertFault = _ => new TimeoutException("insert timed out");

        var act = () => h.Announcer.ProcessAsync(h.Pipeline, h.Inbound("wc-f1"), h.Tenant, h.Channel, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<TimeoutException>("the endpoint fails exactly as before");
        h.ReturnCloseEvents.Should().ContainSingle();
        h.ReturnAuditCount.Should().Be(1);
        h.CustomerReturnedCount.Should().Be(1);
    }

    // ── Audit round 4 P3: an announcer fault on the duplicate branch is logged, never a non-2xx ─────────────────

    [Fact]
    public async Task ProcessAsync_ShouldLogAndCarryOn_WhenAnnouncingTheReturnCloseOfADuplicateFaults()
    {
        using var h = new CustomerReturnHarness();
        var closedId = EntityId.New();
        var next = await h.SeedAsync(ConversationState.Queued, idle: TimeSpan.Zero, ownerless: true);
        var pipeline = Substitute.For<IInboundMessagePipeline>();
        var duplicate = h.Inbound("wamid.p3-dup");
        var fresh = h.Inbound("wamid.p3-next");
        pipeline.ProcessAsync(duplicate, h.Tenant, h.Channel, Arg.Any<CancellationToken>()).Returns(
            new PipelineResult(EntityId.New(), h.Contact.ContactId, EntityId.New(), IsNewConversation: false,
                IsDuplicate: true, ClosedOnReturnId: closedId));
        pipeline.ProcessAsync(fresh, h.Tenant, h.Channel, Arg.Any<CancellationToken>()).Returns(
            new PipelineResult(next.ConversationId, h.Contact.ContactId, EntityId.New(), IsNewConversation: true));
        var fault = new InvalidOperationException("SSE subscriber failed");
        using var faulty = h.EventBus.Events.Subscribe(e =>
        {
            if (e is ConversationStateChangedEvent { OldState: nameof(ConversationState.Resolved) })
                throw fault;
        });

        var act = () => h.CreateProcessor(pipeline).ProcessAsync(h.Tenant, h.Channel, Delivery(duplicate, fresh), CancellationToken.None);

        await act.Should().NotThrowAsync("the messages are stored; an announcement fault must not make the provider retry");
        h.AssignmentsOf(next.ConversationId).Should().Be(1, "the next message's side effects still run");
        h.AnnouncerLogger.Entries.Concat(h.ProcessorLogger.Entries)
            .Should().Contain(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning && ReferenceEquals(e.Exception, fault),
                "the fault is logged like any other side-effect failure");
        h.ReturnAuditCount.Should().Be(1, "a failed publish does not skip the audit entry");
        h.CustomerReturnedCount.Should().Be(1);
    }

    private static InactivityCloseRequest InactivityRequest(CustomerReturnHarness h, Conversation read) => new(
        read.ConversationId, read.State, read.Owner,
        Cutoff: h.Now - TimeSpan.FromHours(72),
        BacklogCutoff: h.Now - TimeSpan.FromHours(73) - TimeSpan.FromMinutes(1),
        Now: h.Now);

    /// <summary>Records every result; the (e5) control drops the flag on every duplicate, as the old D8 wording did.</summary>
    private sealed class RecordingPipeline(IInboundMessagePipeline inner) : IInboundMessagePipeline
    {
        private readonly List<PipelineResult> _results = [];

        public bool DropFlagOnDuplicate { get; init; }

        public IReadOnlyList<PipelineResult> Results
        {
            get { lock (_results) return [.. _results]; }
        }

        public async Task<PipelineResult> ProcessAsync(InboundMessage message, TenantId tenantId, ChannelType channel, CancellationToken ct)
        {
            var result = await inner.ProcessAsync(message, tenantId, channel, ct);
            if (DropFlagOnDuplicate && result.IsDuplicate)
                result = result with { ClosedOnReturnId = null };
            lock (_results) _results.Add(result);
            return result;
        }
    }
}
