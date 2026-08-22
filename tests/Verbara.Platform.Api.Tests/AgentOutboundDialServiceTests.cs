using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Compliance;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Trunk = Verbara.Sdk.Pro.Dialer.Models.Trunk;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// 3B.2d agent click-to-dial. Verifies the leader gate, DNC compliance, route→trunk resolution
/// (resolver + default-trunk fallback), the tenant outbound caller-ID, the exact OriginateAction
/// emitted (agent A-leg + [outbound-agent] + correlation/trunk/caller-id channel vars), and that the
/// tracked outbound Conversation is created (direction=outbound, owner=agent) only on success.
/// </summary>
public sealed class AgentOutboundDialServiceTests
{
    private const string Tenant = "acme";
    private const string DefaultTrunk = "pstn-default";
    private static readonly TenantId TenantId = new(Tenant);

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IContactIdentityResolver _contacts = Substitute.For<IContactIdentityResolver>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly FakeOriginateExecutor _executor = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly EntityId _agentId = EntityId.New();

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
        OutboundRouteResolverBase? routes = null,
        DncCheckerBase? dnc = null,
        string? defaultTrunk = DefaultTrunk)
    {
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isLeader);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        return new AgentOutboundDialService(
            _conversations, _contacts, _agents, _tenants, _executor, routes, dnc, leader, _clock,
            defaultTrunk, NullLogger<AgentOutboundDialService>.Instance);
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
        Vars(action).Should().Contain("TRUNK=pstn-default");
        Vars(action).Should().Contain("OUTBOUND_CALLERID=+15558675309");
        Vars(action).Should().Contain("TENANT_ID=acme");
    }

    [Fact]
    public async Task DialAsync_ShouldPreferResolvedRouteTrunk_OverDefault()
    {
        var sut = CreateService(routes: new FakeRouteResolver { Trunk = new Trunk { Id = 1, Name = "premium-sip" } });

        var outcome = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        outcome.Accepted.Should().BeTrue();
        Vars(_executor.LastAction!).Should().Contain("TRUNK=premium-sip");
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

    private sealed class FakeRouteResolver : OutboundRouteResolverBase
    {
        public Trunk? Trunk;

        public override ValueTask<Trunk?> ResolveAsync(
            string tenantId, string phoneNumber, long? campaignId, CancellationToken ct) =>
            ValueTask.FromResult(Trunk);
    }
}
