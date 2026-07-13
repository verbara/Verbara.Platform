using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Trunk = Verbara.Sdk.Pro.Dialer.Models.Trunk;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// 3B.2c blind transfer. Verifies the leader gate, the customer-channel correlation, and the exact
/// AMI Setvar + Redirect emitted per target kind (queue → [stasis-queue]; agent → [transfer-agent]).
/// </summary>
public sealed class VoiceCallControlServiceTests : IDisposable
{
    private const string Tenant = "acme";
    private const string CustomerChannel = "PJSIP/acme-trunk-2-00000001";
    private static readonly TenantId TenantId = new(Tenant);

    private const string DefaultTrunk = "pstn-default";

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IQueueStore _queues = Substitute.For<IQueueStore>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly VerbaraServerPool _serverPool =
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);

    public void Dispose() => _serverPool.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private VoiceCallControlService CreateService(
        bool isLeader = true,
        bool withServer = true,
        OutboundRouteResolverBase? routes = null,
        string? defaultTrunk = DefaultTrunk)
    {
        if (withServer)
            _serverPool.AddExistingServer("primary", new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));
        _tenants.GetAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = Tenant, Name = "Acme", Options = new TenantOptions { OutboundCallerId = "+15558675309" } });
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isLeader);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        return new VoiceCallControlService(
            _conversations, _queues, _agents, _serverPool, routes, _tenants, leader, defaultTrunk,
            NullLogger<VoiceCallControlService>.Instance);
    }

    private static Conversation VoiceConversation(bool withChannel = true, EntityId? owner = null)
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = TenantId,
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (owner is not null)
            conv.Owner = new ConversationOwner(ConversationOwnerKind.Agent, owner);
        if (withChannel)
            conv.SetMetadata("customerChannel", CustomerChannel);
        return conv;
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldReturnNotLeader_WhenNotLeader()
    {
        var sut = CreateService(isLeader: false);
        var outcome = await sut.BlindTransferAsync(
            TenantId, EntityId.New(), new VoiceTransferTarget(VoiceTransferKind.Queue, "q1"), CancellationToken.None);
        outcome.Accepted.Should().BeFalse();
        outcome.Error.Should().Be("not-leader");
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldReturnChannelUnknown_WhenCustomerChannelMissing()
    {
        var conv = VoiceConversation(withChannel: false);
        _conversations.GetByIdAsync(TenantId, conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        var sut = CreateService();

        var outcome = await sut.BlindTransferAsync(
            TenantId, conv.ConversationId, new VoiceTransferTarget(VoiceTransferKind.Queue, "q1"), CancellationToken.None);

        outcome.Error.Should().Be("channel-unknown");
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldSetQueueNameAndRedirectToStasisQueue_WhenQueueTarget()
    {
        var conv = VoiceConversation();
        _conversations.GetByIdAsync(TenantId, conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        var queueId = EntityId.New();
        _queues.GetByIdAsync(TenantId, queueId, Arg.Any<CancellationToken>()).Returns(new Queue
        {
            QueueId = queueId, TenantId = TenantId, Name = "Sales", CreatedAt = DateTimeOffset.UtcNow,
        });
        var sut = CreateService();

        var outcome = await sut.BlindTransferAsync(
            TenantId, conv.ConversationId, new VoiceTransferTarget(VoiceTransferKind.Queue, queueId.Value), CancellationToken.None);

        outcome.Accepted.Should().BeTrue();
        await _ami.Received().SendActionAsync(
            Arg.Is<SetVarAction>(v => v.Channel == CustomerChannel
                && v.Variable == "QUEUE_NAME" && v.Value == "acme-Sales"),
            Arg.Any<CancellationToken>());
        await _ami.Received().SendActionAsync(
            Arg.Is<RedirectAction>(r => r.Channel == CustomerChannel
                && r.Context == "stasis-queue" && r.Exten == "s"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldDialAgentAndRedirectToTransferAgent_WhenAgentTarget()
    {
        var conv = VoiceConversation();
        _conversations.GetByIdAsync(TenantId, conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        var agentId = EntityId.New();
        _agents.GetByIdAsync(TenantId, agentId, Arg.Any<CancellationToken>()).Returns(new Agent
        {
            AgentId = agentId, TenantId = TenantId, UserId = EntityId.New(),
            DisplayName = "A", State = AgentState.Available, CreatedAt = DateTimeOffset.UtcNow,
        });
        var sut = CreateService();

        var outcome = await sut.BlindTransferAsync(
            TenantId, conv.ConversationId, new VoiceTransferTarget(VoiceTransferKind.Agent, agentId.Value), CancellationToken.None);

        outcome.Accepted.Should().BeTrue();
        await _ami.Received().SendActionAsync(
            Arg.Is<SetVarAction>(v => v.Variable == "TRANSFER_TARGET"
                && v.Value == $"PJSIP/acme-agent-{agentId.Value}"),
            Arg.Any<CancellationToken>());
        await _ami.Received().SendActionAsync(
            Arg.Is<RedirectAction>(r => r.Context == "transfer-agent" && r.Exten == "s"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldReturnQueueNotFound_WhenQueueMissing()
    {
        var conv = VoiceConversation();
        _conversations.GetByIdAsync(TenantId, conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        _queues.GetByIdAsync(TenantId, Arg.Any<EntityId>(), Arg.Any<CancellationToken>()).Returns((Queue?)null);
        var sut = CreateService();

        var outcome = await sut.BlindTransferAsync(
            TenantId, conv.ConversationId, new VoiceTransferTarget(VoiceTransferKind.Queue, EntityId.New().Value), CancellationToken.None);

        outcome.Error.Should().Be("queue-not-found");
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldRedirectToOutboundAgentWithTrunkAndCallerId_WhenExternalTarget()
    {
        var conv = VoiceConversation();
        _conversations.GetByIdAsync(TenantId, conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        var sut = CreateService();

        var outcome = await sut.BlindTransferAsync(
            TenantId, conv.ConversationId, new VoiceTransferTarget(VoiceTransferKind.External, "+1 (555) 999-8888"), CancellationToken.None);

        outcome.Accepted.Should().BeTrue();
        await _ami.Received().SendActionAsync(
            Arg.Is<SetVarAction>(v => v.Channel == CustomerChannel && v.Variable == "TRUNK" && v.Value == "pstn-default"),
            Arg.Any<CancellationToken>());
        await _ami.Received().SendActionAsync(
            Arg.Is<SetVarAction>(v => v.Variable == "OUTBOUND_CALLERID" && v.Value == "+15558675309"),
            Arg.Any<CancellationToken>());
        await _ami.Received().SendActionAsync(
            Arg.Is<RedirectAction>(r => r.Channel == CustomerChannel
                && r.Context == "outbound-agent" && r.Exten == "+15559998888"), // normalized
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldPreferResolvedTrunk_WhenExternalWithRouteResolver()
    {
        var conv = VoiceConversation();
        _conversations.GetByIdAsync(TenantId, conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        var sut = CreateService(routes: new FakeRouteResolver { Trunk = new Trunk { Id = 1, Name = "premium-sip" } });

        var outcome = await sut.BlindTransferAsync(
            TenantId, conv.ConversationId, new VoiceTransferTarget(VoiceTransferKind.External, "+15559998888"), CancellationToken.None);

        outcome.Accepted.Should().BeTrue();
        await _ami.Received().SendActionAsync(
            Arg.Is<SetVarAction>(v => v.Variable == "TRUNK" && v.Value == "premium-sip"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldReturnNoTrunk_WhenExternalAndNoTrunkConfigured()
    {
        var conv = VoiceConversation();
        _conversations.GetByIdAsync(TenantId, conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        var sut = CreateService(defaultTrunk: null);

        var outcome = await sut.BlindTransferAsync(
            TenantId, conv.ConversationId, new VoiceTransferTarget(VoiceTransferKind.External, "+15559998888"), CancellationToken.None);

        outcome.Error.Should().Be("no-trunk");
    }

    // ─── csat-completion — survey-IVR handoff (Platform/ADR-0020) ─────────────────

    [Fact]
    public async Task BlindTransferAsync_ShouldSetSurveyVarsAndRedirectToSurveyIvr_WhenSurveyIvrTarget()
    {
        var conv = VoiceConversation();
        _conversations.GetByIdAsync(TenantId, conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        var sut = CreateService();

        var outcome = await sut.BlindTransferAsync(
            TenantId, conv.ConversationId,
            new VoiceTransferTarget(VoiceTransferKind.SurveyIvr, "srv-csat-v1", Token: "v1.payload.sig"),
            CancellationToken.None);

        outcome.Accepted.Should().BeTrue();
        await _ami.Received().SendActionAsync(
            Arg.Is<SetVarAction>(v => v.Channel == CustomerChannel && v.Variable == "SURVEY_ID" && v.Value == "srv-csat-v1"),
            Arg.Any<CancellationToken>());
        await _ami.Received().SendActionAsync(
            Arg.Is<SetVarAction>(v => v.Variable == "SURVEY_TOKEN" && v.Value == "v1.payload.sig"),
            Arg.Any<CancellationToken>());
        await _ami.Received().SendActionAsync(
            Arg.Is<RedirectAction>(r => r.Channel == CustomerChannel
                && r.Context == "survey-ivr" && r.Exten == "s"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldReturnNotLeader_WhenSurveyIvrAndNotLeader()
    {
        var sut = CreateService(isLeader: false);

        var outcome = await sut.BlindTransferAsync(
            TenantId, EntityId.New(),
            new VoiceTransferTarget(VoiceTransferKind.SurveyIvr, "srv-csat-v1", Token: "v1.payload.sig"),
            CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Error.Should().Be("not-leader");
    }

    [Fact]
    public async Task BlindTransferAsync_ShouldReturnChannelUnknown_WhenSurveyIvrAndCustomerChannelMissing()
    {
        var conv = VoiceConversation(withChannel: false);
        _conversations.GetByIdAsync(TenantId, conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        var sut = CreateService();

        var outcome = await sut.BlindTransferAsync(
            TenantId, conv.ConversationId,
            new VoiceTransferTarget(VoiceTransferKind.SurveyIvr, "srv-csat-v1", Token: "v1.payload.sig"),
            CancellationToken.None);

        outcome.Error.Should().Be("channel-unknown");
        await _ami.DidNotReceive().SendActionAsync(Arg.Any<RedirectAction>(), Arg.Any<CancellationToken>());
    }

    private sealed class FakeRouteResolver : OutboundRouteResolverBase
    {
        public Trunk? Trunk;

        public override ValueTask<Trunk?> ResolveAsync(
            string tenantId, string phoneNumber, long? campaignId, CancellationToken ct) =>
            ValueTask.FromResult(Trunk);
    }
}
