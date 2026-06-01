using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

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

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IQueueStore _queues = Substitute.For<IQueueStore>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly VerbaraServerPool _serverPool =
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);

    public void Dispose() => _serverPool.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private VoiceCallControlService CreateService(bool isLeader = true, bool withServer = true)
    {
        if (withServer)
            _serverPool.AddExistingServer("primary", new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isLeader);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        return new VoiceCallControlService(
            _conversations, _queues, _agents, _serverPool, leader, NullLogger<VoiceCallControlService>.Instance);
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
}
