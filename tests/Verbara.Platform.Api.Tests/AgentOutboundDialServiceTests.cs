using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.OutboundTrunk;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Compliance;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.MultiTenant;
using Trunk = Verbara.Sdk.Pro.Dialer.Models.Trunk;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Verbara.Platform.Api.Tests.OutboundTrunk.TrunkFakes;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// 3B.2d agent click-to-dial. Verifies the leader gate, DNC compliance, route→trunk resolution through the
/// real <see cref="Verbara.Platform.Api.Services.OutboundTrunk.DialableTrunkResolver"/> over a fake route store
/// and trunk store (route trunk, else the default trunk by name), the tenant outbound caller-ID, the exact OriginateAction
/// emitted (agent A-leg + [outbound-agent] + correlation/trunk/caller-id channel vars), and that the
/// tracked outbound Conversation is created (direction=outbound, owner=agent) only on success.
/// </summary>
public sealed class AgentOutboundDialServiceTests
{
    private const string Tenant = "acme";
    private const string DefaultTrunk = "pstn-default";
    private const string DefaultHost = "198.51.100.7";
    private static readonly TenantId TenantId = new(Tenant);

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly IContactIdentityResolver _contacts = Substitute.For<IContactIdentityResolver>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly FakeOriginateExecutor _executor = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly EntityId _agentId = EntityId.New();
    private readonly FakeTrunkStore _trunks = new FakeTrunkStore()
        .Add(Tenant, IpTrunk(7, DefaultTrunk, DefaultHost))
        .Add(Tenant, new Trunk { Id = 1, Name = "premium-sip", MatchHost = RouteHost, IsActive = true })
        .Add(Tenant, AuthOnlyTrunk(4, "auth-only"));

    public AgentOutboundDialServiceTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        _agents.GetByIdAsync(TenantId, _agentId, Arg.Any<CancellationToken>()).Returns(new Agent
        {
            AgentId = _agentId, TenantId = TenantId, UserId = EntityId.New(),
            DisplayName = "A", State = AgentState.Available, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        _contacts.ResolveAsync(TenantId, Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>())
            .Returns(new Contact { ContactId = EntityId.New(), TenantId = TenantId, CreatedAt = DateTimeOffset.UnixEpoch });
        _tenants.GetAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = Tenant, Name = "Acme", Options = new TenantOptions { OutboundCallerId = "+15558675309" } });
    }

    private AgentOutboundDialService CreateService(
        bool isLeader = true,
        FakeRouteStore? routes = null,
        DncCheckerBase? dnc = null,
        string? defaultTrunk = DefaultTrunk)
    {
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isLeader);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        return new AgentOutboundDialService(
            _conversations, _capacity, _contacts, _agents, _tenants, _executor, Resolver(routes, _trunks, defaultTrunk), dnc, leader,
            _clock, NullLogger<AgentOutboundDialService>.Instance);
    }

    private static IEnumerable<string> Vars(OriginateAction action) =>
        ((IHasExtraFields)action).GetExtraFields().Select(kv => kv.Value);

    [Fact]
    public async Task DialAsync_ShouldReturnNotLeader_WhenNotLeader()
    {
        var sut = CreateService(isLeader: false);
        var outcome = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);
        outcome.Accepted.Should().BeFalse();
        outcome.Error.Should().Be("not-leader");
    }

    [Fact]
    public async Task DialAsync_ShouldReturnAgentNotFound_WhenAgentMissing()
    {
        var sut = CreateService();
        var outcome = await sut.DialAsync(TenantId, EntityId.New(), "+15551234567", null, CancellationToken.None);
        outcome.Error.Should().Be("agent-not-found");
    }

    [Fact]
    public async Task DialAsync_ShouldReturnDncBlocked_WhenNumberBlocked()
    {
        var sut = CreateService(dnc: new FakeDncChecker { Blocked = true });

        var outcome = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        outcome.Error.Should().Be("dnc-blocked");
        _executor.LastAction.Should().BeNull("a blocked number must never be originated");
    }

    [Fact]
    public async Task DialAsync_ShouldReturnNoTrunk_WhenNoRouteAndNoDefault()
    {
        var sut = CreateService(defaultTrunk: null);
        var outcome = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);
        outcome.Error.Should().Be("no-trunk");
    }

    [Fact]
    public async Task DialAsync_ShouldOriginateAgentLegToOutboundContext_WhenValid()
    {
        var sut = CreateService();

        var outcome = await sut.DialAsync(TenantId, _agentId, "+1 (555) 123-4567", null, CancellationToken.None);

        outcome.Accepted.Should().BeTrue();
        outcome.CorrelationId.Should().NotBeNullOrEmpty();
        var action = _executor.LastAction!;
        _executor.LastNode.Should().Be("primary");
        action.Channel.Should().Be($"PJSIP/acme-agent-{_agentId.Value}");
        action.Context.Should().Be("outbound-agent");
        action.Exten.Should().Be("+15551234567"); // normalized
        action.IsAsync.Should().BeTrue();
        Vars(action).Should().Contain($"VERBARA_OUTBOUND_ID={outcome.CorrelationId}");
        Vars(action).Should().Contain("TRUNK=t-7", "the default trunk is looked up by name and dialed through its endpoint id");
        Vars(action).Should().Contain($"TRUNK_HOST={DefaultHost}");
        Vars(action).Should().Contain("OUTBOUND_CALLERID=+15558675309");
        Vars(action).Should().Contain("TENANT_ID=acme");
    }

    [Fact]
    public async Task DialAsync_ShouldPreferResolvedRouteTrunk_OverDefault()
    {
        var sut = CreateService(routes: new FakeRouteStore().Add(Tenant, Route(1, "+1", trunkId: 1)));

        var outcome = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        outcome.Accepted.Should().BeTrue();
        Vars(_executor.LastAction!).Should().Contain("TRUNK=t-1", "the trunk is dialed through its endpoint id, never its name");
        Vars(_executor.LastAction!).Should().Contain("TRUNK_HOST=203.0.113.10", "the host comes from the tenant's stored trunk");
    }

    [Fact]
    public async Task DialAsync_ShouldKeepTheLeadingPlusInTheExten_WhenTheNumberIsE164()
    {
        var sut = CreateService();

        var outcome = await sut.DialAsync(TenantId, _agentId, "+573001234567", null, CancellationToken.None);

        outcome.Accepted.Should().BeTrue();
        _executor.LastAction!.Exten.Should().Be("+573001234567", "the dialplan matches _+X., so the + is dialed as typed");
    }

    [Fact]
    public async Task DialAsync_ShouldRefuseTrunkNotDialable_WithoutOriginating_WhenTheTrunkHasNoHost()
    {
        var sut = CreateService(routes: new FakeRouteStore().Add(Tenant, Route(1, "+57", trunkId: 4)));

        var outcome = await sut.DialAsync(TenantId, _agentId, "+573001234567", null, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Error.Should().Be("trunk-not-dialable");
        outcome.TrunkId.Should().Be(4);
        _executor.LastAction.Should().BeNull("the agent's softphone must not ring for a call that cannot reach the carrier");
        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DialAsync_ShouldReturnNoTrunk_WhenTheDefaultNamesNoStoredTrunk()
    {
        var sut = CreateService(defaultTrunk: "t-1");

        var outcome = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        outcome.Error.Should().Be("no-trunk", "DefaultTrunk is a trunk name, never an endpoint id");
        _executor.LastAction.Should().BeNull();
    }

    [Fact]
    public async Task DialAsync_ShouldCreateOutboundConversation_WhenOriginateSucceeds()
    {
        var sut = CreateService();

        var outcome = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        await _conversations.Received(1).SaveAsync(
            Arg.Is<Conversation>(c => c != null &&
                c.ConversationId.Value == outcome.CorrelationId
                && c.Channel == ChannelType.Voice
                && c.Owner!.Kind == ConversationOwnerKind.Agent
                && c.Owner.OwnerId == _agentId
                && c.Metadata.ContainsKey("direction")
                && c.Metadata["direction"] == "outbound"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DialAsync_ShouldReserveVoiceCapacityBeforeSavingTheActiveConversation_WhenOriginateSucceeds()
    {
        var sut = CreateService();

        await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        // The capacity service rebuilds its load from the saved Active Conversations on its first use, so the
        // reservation must run before the Active row exists or that rebuild counts the call a second time (#360).
        Received.InOrder(() =>
        {
            _capacity.ReserveAsync(TenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>());
            _conversations.SaveAsync(Arg.Is<Conversation>(c => c != null && c.State == ConversationState.Active), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task DialAsync_ShouldReleaseTheReservation_WhenSavingTheConversationFails()
    {
        _conversations.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store down"));
        var sut = CreateService();

        var act = () => sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _capacity.Received(1).ReserveAsync(TenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>());
        await _capacity.Received(1).ReleaseAsync(TenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DialAsync_ShouldNotReserveCapacity_WhenOriginateRejected()
    {
        _executor.Result = new OriginateResult(false, null, "trunk-down");
        var sut = CreateService();

        await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        await _capacity.DidNotReceive().ReserveAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DialAsync_ShouldNotCreateConversation_WhenOriginateRejected()
    {
        _executor.Result = new OriginateResult(false, null, "trunk-down");
        var sut = CreateService();

        var outcome = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Error.Should().Be("trunk-down");
        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DialAsync_ShouldFailOpenOnDncError_AndStillOriginate()
    {
        var sut = CreateService(dnc: new FakeDncChecker { Throw = true });

        var outcome = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        outcome.Accepted.Should().BeTrue("DNC errors fail open so a transient store outage never blocks dialing");
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

    private sealed class FakeDncChecker : DncCheckerBase
    {
        public bool Blocked;
        public bool Throw;

        public override ValueTask<bool> IsBlockedAsync(
            string tenantId, string phoneNumber, long? dncListId, bool checkGlobal, CancellationToken ct) =>
            Throw ? throw new InvalidOperationException("dnc store down") : ValueTask.FromResult(Blocked);
    }
}
