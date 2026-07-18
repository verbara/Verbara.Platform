using System.Globalization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Trunk = Verbara.Sdk.Pro.Dialer.Models.Trunk;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// W5b voice caller-rescue originator. Verifies the rescue callback dials the customer DIRECTLY over the
/// resolved trunk into the inbound <c>[stasis-queue]</c> contract, sets the QUEUE_NAME + correlation +
/// front-of-queue (QUEUE_PRIO) channel vars, and persists a tracked Voice/Queued rescue Conversation
/// (direction=callback-rescue, rescuedFrom + callbackAttempts metadata, ContactId reused from the original)
/// ONLY on a successful Originate.
/// </summary>
public sealed class CallbackOriginatorTests
{
    private const string Tenant = "t1";
    private const string DefaultTrunk = "pstn-default";
    private static readonly TenantId TenantId = new(Tenant);

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IQueueStore _queues = Substitute.For<IQueueStore>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly IContactIdentityResolver _contacts = Substitute.For<IContactIdentityResolver>();
    private readonly FakeOriginateExecutor _executor = new();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly EntityId _queueId = EntityId.New();
    private readonly EntityId _rescuedFrom = EntityId.New();
    private readonly EntityId _originalContactId = EntityId.New();

    public CallbackOriginatorTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        _queues.GetByIdAsync(TenantId, _queueId, Arg.Any<CancellationToken>())
            .Returns(new Queue { QueueId = _queueId, TenantId = TenantId, Name = "Support", CreatedAt = DateTimeOffset.UnixEpoch });
        _tenants.GetAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = Tenant, Name = "T1", Options = new TenantOptions { OutboundCallerId = "+15558675309" } });
        _conversations.GetByIdAsync(TenantId, _rescuedFrom, Arg.Any<CancellationToken>())
            .Returns(new Conversation
            {
                ConversationId = _rescuedFrom, TenantId = TenantId, ContactId = _originalContactId,
                Channel = ChannelType.Voice, State = ConversationState.WrapUp, CreatedAt = DateTimeOffset.UnixEpoch,
            });
    }

    private CallbackOriginator CreateService(
        OutboundRouteResolverBase? routes = null,
        string? defaultTrunk = DefaultTrunk) =>
        new(_conversations, _queues, _tenants, _contacts, _executor, routes, _clock,
            defaultTrunk, NullLogger<CallbackOriginator>.Instance);

    private static IEnumerable<string> Vars(OriginateAction action) =>
        ((IHasExtraFields)action).GetExtraFields().Select(kv => kv.Value);

    [Fact]
    public async Task OriginateCallbackAsync_ShouldDialCustomerIntoStasisQueue_WhenSuccessful()
    {
        var sut = CreateService();

        var accepted = await sut.OriginateCallbackAsync(
            TenantId, "+1 (555) 123-4567", _queueId, _rescuedFrom, 1, CancellationToken.None);

        accepted.Should().BeTrue();
        var action = _executor.LastAction!;
        _executor.LastNode.Should().Be("primary");
        action.Channel.Should().Be("PJSIP/pstn-default/+15551234567"); // dialed directly, normalized
        action.Context.Should().Be("stasis-queue");
        action.Exten.Should().Be("s");
        action.IsAsync.Should().BeTrue();
        action.CallerId.Should().Be("+15558675309");
    }

    [Fact]
    public async Task OriginateCallbackAsync_ShouldSetQueueNameAndRescueVars_WhenSuccessful()
    {
        Conversation? saved = null;
        await _conversations.SaveAsync(Arg.Do<Conversation>(c => saved = c), Arg.Any<CancellationToken>());
        var sut = CreateService();

        await sut.OriginateCallbackAsync(TenantId, "+15551234567", _queueId, _rescuedFrom, 1, CancellationToken.None);

        var vars = Vars(_executor.LastAction!).ToList();
        vars.Should().Contain("QUEUE_NAME=t1-Support");
        vars.Should().Contain($"VERBARA_OUTBOUND_ID={saved!.ConversationId.Value}");
        vars.Should().Contain("QUEUE_PRIO=10");
        vars.Should().Contain("TENANT_ID=t1");
    }

    [Fact]
    public async Task OriginateCallbackAsync_ShouldCreateRescueConversation_WhenSuccessful()
    {
        var sut = CreateService();

        await sut.OriginateCallbackAsync(TenantId, "+15551234567", _queueId, _rescuedFrom, 2, CancellationToken.None);

        await _conversations.Received(1).SaveAsync(
            Arg.Is<Conversation>(c =>
                c.Channel == ChannelType.Voice
                && c.State == ConversationState.Queued
                && c.Owner == null
                && c.QueuePriority == -1
                && c.ContactId == _originalContactId
                && c.Metadata["direction"] == "callback-rescue"
                && c.Metadata["rescuedFrom"] == _rescuedFrom.Value
                && c.Metadata["callbackAttempts"] == "2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OriginateCallbackAsync_ShouldResolveContactByNumber_WhenOriginalConversationGone()
    {
        var resolvedContactId = EntityId.New();
        _conversations.GetByIdAsync(TenantId, _rescuedFrom, Arg.Any<CancellationToken>()).Returns((Conversation?)null);
        _contacts.ResolveAsync(TenantId, Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>())
            .Returns(new Contact { ContactId = resolvedContactId, TenantId = TenantId, CreatedAt = DateTimeOffset.UnixEpoch });
        var sut = CreateService();

        await sut.OriginateCallbackAsync(TenantId, "+15551234567", _queueId, _rescuedFrom, 1, CancellationToken.None);

        await _conversations.Received(1).SaveAsync(
            Arg.Is<Conversation>(c => c.ContactId == resolvedContactId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OriginateCallbackAsync_ShouldReturnFalseAndNotSaveConversation_WhenOriginateRejected()
    {
        _executor.Result = new OriginateResult(false, null, "trunk-down");
        var sut = CreateService();

        var accepted = await sut.OriginateCallbackAsync(
            TenantId, "+15551234567", _queueId, _rescuedFrom, 1, CancellationToken.None);

        accepted.Should().BeFalse();
        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OriginateCallbackAsync_ShouldReturnFalse_WhenQueueNotFound()
    {
        _queues.GetByIdAsync(TenantId, _queueId, Arg.Any<CancellationToken>()).Returns((Queue?)null);
        var sut = CreateService();

        var accepted = await sut.OriginateCallbackAsync(
            TenantId, "+15551234567", _queueId, _rescuedFrom, 1, CancellationToken.None);

        accepted.Should().BeFalse();
        _executor.LastAction.Should().BeNull("a missing origin queue must never originate");
    }

    [Fact]
    public async Task OriginateCallbackAsync_ShouldReturnFalse_WhenNoTrunk()
    {
        var sut = CreateService(defaultTrunk: null);

        var accepted = await sut.OriginateCallbackAsync(
            TenantId, "+15551234567", _queueId, _rescuedFrom, 1, CancellationToken.None);

        accepted.Should().BeFalse();
        _executor.LastAction.Should().BeNull("no trunk means no outbound dial");
    }

    [Fact]
    public async Task OriginateCallbackAsync_ShouldReturnFalse_WhenNumberEmpty()
    {
        var sut = CreateService();

        var accepted = await sut.OriginateCallbackAsync(
            TenantId, "()-  ", _queueId, _rescuedFrom, 1, CancellationToken.None);

        accepted.Should().BeFalse();
        _executor.LastAction.Should().BeNull();
    }

    [Fact]
    public async Task OriginateCallbackAsync_ShouldPreferResolvedRouteTrunk_OverDefault()
    {
        var sut = CreateService(routes: new FakeRouteResolver { Trunk = new Trunk { Id = 1, Name = "premium-sip" } });

        await sut.OriginateCallbackAsync(TenantId, "+15551234567", _queueId, _rescuedFrom, 1, CancellationToken.None);

        _executor.LastAction!.Channel.Should().Be("PJSIP/premium-sip/+15551234567");
    }

    [Fact]
    public async Task OriginateCallbackAsync_ShouldStringifyAttemptsInvariant_WhenSuccessful()
    {
        const int attempts = 3;
        var sut = CreateService();

        await sut.OriginateCallbackAsync(TenantId, "+15551234567", _queueId, _rescuedFrom, attempts, CancellationToken.None);

        await _conversations.Received(1).SaveAsync(
            Arg.Is<Conversation>(c => c.Metadata["callbackAttempts"] == attempts.ToString(CultureInfo.InvariantCulture)),
            Arg.Any<CancellationToken>());
    }

    private sealed class FakeOriginateExecutor : OriginateExecutorBase
    {
        public OriginateAction? LastAction;
        public string? LastNode;
        public OriginateResult Result = new(true, "act-1", null);

        // Pro/ADR-0016: the spend-point license check lives on the non-virtual ExecuteAsync
        // template method; derived executors override ExecuteCoreAsync. No guard is passed, so
        // the base allows through to this core unchanged.
        protected override ValueTask<OriginateResult> ExecuteCoreAsync(OriginateAction action, string nodeId, CancellationToken ct)
        {
            LastAction = action;
            LastNode = nodeId;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FakeRouteResolver : OutboundRouteResolverBase
    {
        public Trunk? Trunk;

        public override ValueTask<Trunk?> ResolveAsync(
            string tenantId, string phoneNumber, long? campaignId, CancellationToken ct) =>
            ValueTask.FromResult(Trunk);
    }
}
